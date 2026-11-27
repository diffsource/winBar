// XAML diagnostics TAP that TaskbarTweaker loads into explorer.exe via InitializeXamlDiagnosticsEx.
// Windows 11 draws the taskbar background in XAML on top of the window, which hides any accent set with
// SetWindowCompositionAttribute. This DLL fades out that XAML background so the accent shows through.
// It can also collapse parts of the system tray (clock, quick settings, ...).
// Control: message-only window "TaskbarTweakerTap6".
//   WM_APP:     wParam 1 = hide background, 0 = restore. Reply: number of background elements found.
//   WM_APP + 1: wParam = bit mask of kParts to hide. Reply: number of tray elements found.
//   WM_APP + 2: wParam bit 0 = hide pinned-only buttons, high bits = first N pinned buttons to keep.
//   WM_APP + 3: wParam = four 10-bit stack spans (5-bit start, 5-bit count),
//               lParam bit 0 = reveal all (Alt), bit 1 = pointer over any taskbar,
//               bit 2 = collapse stacks (clear it to show all members when there is room).
//               Positions refer to pinned buttons in their visual order; a stack's first icon is its anchor.
//   WM_APP + 4: wParam = client HWND for stack hover notifications.
//   Client receives WM_APP + 5: wParam = stack slot 0..3, lParam = 1 entered / 0 exited.
//   WM_COPYDATA dwData 'WBM6': uint32 version=1, uint32 count (0..8), then count
//       packed 32-byte entries: uint64 taskbar HWND, uint64 stack spans,
//       uint32 tray mask, uint32 flags, uint32 keep pinned, uint32 reserved=0.
//       Flags: 1 background, 2 pinned, 4 reveal stacks, 8 pointer on this bar,
//       16 collapse stacks. Count=0 clears all per-taskbar overrides.
// A reply of 0 means the XAML tree did not match.
#include <windows.h>
#undef GetCurrentTime // clashes with Storyboard::GetCurrentTime in C++/WinRT
#include <unknwn.h>
#include <ocidl.h>
#include <xamlOM.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Input.h>
#include <winrt/Windows.UI.Xaml.Automation.Peers.h>
#include <winrt/Windows.UI.h>
#include <cstdint>
#include <cmath>
#include <cstring>
#include <memory>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <vector>

#pragma comment(linker, "/export:DllGetClassObject,PRIVATE")
#pragma comment(linker, "/export:DllCanUnloadNow,PRIVATE")

using namespace winrt;
using namespace winrt::Windows::UI::Xaml;

extern "C" IMAGE_DOS_HEADER __ImageBase;

// {3F0F7A5E-8E3B-4C1D-9B2A-6D1E5C7A9B42}, must match TaskbarTweaker's TapClsid.
static constexpr CLSID CLSID_Tap = { 0x3f0f7a5e, 0x8e3b, 0x4c1d, { 0x9b, 0x2a, 0x6d, 0x1e, 0x5c, 0x7a, 0x9b, 0x42 } };

// Tray parts, matched by XAML type and name (Windows 11 24H2+/26xxx tree). bit is the WM_APP + 1 mask bit.
struct Part { const wchar_t* type; const wchar_t* name; int bit; };
static constexpr Part kParts[] =
{
    { L"SystemTray.OmniButton", L"NotificationCenterButton", 1 },         // clock/date (+ bell)
    { L"SystemTray.OmniButton", L"ControlCenterButton", 2 },              // Wi-Fi / volume / battery
    { L"SystemTray.NotificationAreaIcons", L"NotificationAreaIcons", 4 }, // app tray icons
    { L"SystemTray.Stack", L"NotifyIconStack", 4 },                       // ^ overflow chevron
    { L"SystemTray.Stack", L"NonActivatableStack", 8 },                   // language indicator
    { L"SystemTray.SystemTrayFrame", L"", 16 },                           // whole tray
};

struct Item
{
    InstanceHandle handle;
    weak_ref<UIElement> element;
    winrt::Windows::System::DispatcherQueue queue{ nullptr };
    int bit; // 0 = taskbar background, else kParts bit
    void* rootIdentity = nullptr;
};

struct PinnedItem
{
    InstanceHandle handle; // IconPanel visual-tree handle
    weak_ref<FrameworkElement> button;
    weak_ref<FrameworkElement> iconPanel;
    weak_ref<winrt::Windows::UI::Xaml::Controls::Border> stackBadge;
    weak_ref<VisualStateGroup> runningStates;
    void* parentIdentity = nullptr; // UI-thread parent identity, used only for sibling ordering
    winrt::Windows::System::DispatcherQueue queue{ nullptr };
    bool hiddenByUs = false; // touched only on the button's UI thread
    int stackIndex = -1; // UI thread: set by ApplyPinned for PointerEntered
    void* rootIdentity = nullptr;
};

#pragma pack(push, 1)
struct MonitorWireEntry
{
    uint64_t taskbarHwnd;
    uint64_t stackRanges;
    uint32_t trayMask;
    uint32_t flags; // background, pinned, Alt reveal, pointer, stack collapse
    uint32_t keepPinned;
    uint32_t reserved;
};
#pragma pack(pop)
static_assert(sizeof(MonitorWireEntry) == 32);

struct MonitorConfig
{
    HWND taskbar = nullptr;
    uint64_t stackRanges = 0;
    int trayMask = 0;
    bool hideBackground = false;
    bool hidePinned = false;
    bool revealStacks = false;
    bool pointerOnBar = false;
    bool collapseStacks = false;
    int keepPinned = 0;
};

static std::mutex g_lock;
static std::vector<Item> g_items;
static std::vector<std::shared_ptr<PinnedItem>> g_pinnedItems;
static std::unordered_map<void*, HWND> g_rootOwners;
static std::vector<MonitorConfig> g_monitorConfigs;
static bool g_hidden = true;
static int g_mask;
static bool g_pinnedHidden;
static int g_keepPinned;
static uint64_t g_stackSpans;
static bool g_stackRevealAll;
static bool g_stackCollapseActive;
static int g_hoveredStack = -1;
static void* g_hoveredParent;
static void* g_hoveredRoot;
static HWND g_hoverReceiver;
static HWND g_wnd;

static HWND const kAmbiguousOwner = reinterpret_cast<HWND>(static_cast<intptr_t>(-1));

static uint64_t SanitizeSpans(uint64_t requested)
{
    uint64_t accepted = 0;
    bool occupied[32]{};
    for (int group = 0; group < 4; group++)
    {
        auto span = static_cast<int>((requested >> (group * 10)) & 0x3ff);
        int start = span & 31;
        int count = (span >> 5) & 31;
        if (count < 2 || start + count > 32) continue;
        bool overlaps = false;
        for (int i = start; i < start + count; i++) if (occupied[i]) overlaps = true;
        if (overlaps) continue;
        for (int i = start; i < start + count; i++) occupied[i] = true;
        accepted |= static_cast<uint64_t>(span) << (group * 10);
    }
    return accepted;
}

// Caller holds g_lock. Unresolved or ambiguous elements retain the existing global behavior.
static MonitorConfig ConfigForRoot(void* root)
{
    MonitorConfig config;
    config.hideBackground = g_hidden;
    config.trayMask = g_mask;
    config.hidePinned = g_pinnedHidden;
    config.keepPinned = g_keepPinned;
    config.stackRanges = g_stackSpans;
    config.revealStacks = g_stackRevealAll;
    config.collapseStacks = g_stackCollapseActive;
    config.pointerOnBar = true;
    if (g_monitorConfigs.empty() || !root) return config;
    auto owner = g_rootOwners.find(root);
    if (owner == g_rootOwners.end() || !owner->second || owner->second == kAmbiguousOwner) return config;
    for (auto const& override : g_monitorConfigs)
        if (override.taskbar == owner->second) return override;
    return config;
}

static bool IsHidden(int bit, void* root)
{
    auto config = ConfigForRoot(root);
    return bit == 0 ? config.hideBackground : (config.trayMask & bit) != 0;
} // caller holds g_lock

static void* RootIdentity(UIElement const& element)
{
    try { if (auto root = element.XamlRoot()) return get_abi(root); }
    catch (...) {}
    return nullptr;
}

// Runs only on the element's XAML UI thread. A peer can be missing on internal Windows controls.
static HWND OwnerFromElement(UIElement const& element)
{
    try
    {
        DependencyObject current = element;
        for (int depth = 0; current && depth < 8; depth++)
        {
            if (auto framework = current.try_as<FrameworkElement>())
            {
                auto peer = winrt::Windows::UI::Xaml::Automation::Peers::FrameworkElementAutomationPeer::CreatePeerForElement(framework);
                if (peer)
                {
                    auto rect = peer.GetBoundingRectangle();
                    if (std::isfinite(rect.X) && std::isfinite(rect.Y) && std::isfinite(rect.Width) && std::isfinite(rect.Height) &&
                        rect.Width > 0 && rect.Height > 0 && std::abs(rect.X) < 100000 && std::abs(rect.Y) < 100000)
                    {
                        POINT center{ static_cast<LONG>(rect.X + rect.Width / 2), static_cast<LONG>(rect.Y + rect.Height / 2) };
                        HWND found = nullptr;
                        auto check = [&](HWND bar)
                        {
                            RECT bounds{};
                            if (bar && GetWindowRect(bar, &bounds) && PtInRect(&bounds, center))
                                found = found ? kAmbiguousOwner : bar;
                        };
                        check(FindWindowW(L"Shell_TrayWnd", nullptr));
                        HWND secondary = nullptr;
                        while ((secondary = FindWindowExW(nullptr, secondary, L"Shell_SecondaryTrayWnd", nullptr)) != nullptr)
                            check(secondary);
                        if (found) return found;
                    }
                }
            }
            current = Media::VisualTreeHelper::GetParent(current);
        }
    }
    catch (...) {}
    return nullptr;
}

static bool RememberOwner(void* root, HWND owner)
{
    if (!root || !owner) return false;
    std::lock_guard lock(g_lock);
    auto [it, inserted] = g_rootOwners.emplace(root, owner);
    if (inserted) return true;
    if (it->second == owner || it->second == kAmbiguousOwner) return false;
    it->second = kAmbiguousOwner;
    return true;
}

// Runs on the element's UI thread.
// shortcut: un-hiding sets Visible instead of restoring explorer's own value; restart explorer if a part looks wrong.
static void Apply(UIElement const& e, int bit, bool hidden)
{
    if (bit == 0) e.Opacity(hidden ? 0.0 : 1.0);
    else e.Visibility(hidden ? Visibility::Collapsed : Visibility::Visible);
}

// The taskbar's RunningIndicatorStates group reports NoRunningIndicator for pinned apps with no
// running window. Unknown states stay visible so a Windows update cannot hide running apps by default.
static int StackForPosition(int position, uint64_t spans, int* anchor = nullptr, int* size = nullptr)
{
    for (int group = 0; group < 4; group++)
    {
        auto span = static_cast<int>((spans >> (group * 10)) & 0x3ff);
        int start = span & 31;
        int count = (span >> 5) & 31;
        if (count >= 2 && position >= start && position < start + count)
        {
            if (anchor) *anchor = start;
            if (size) *size = count;
            return group;
        }
    }
    return -1;
}

static void ApplyPinned(std::shared_ptr<PinnedItem> const& item)
{
    auto button = item->button.get();
    auto group = item->runningStates.get();
    if (!button || !group) return;
    auto state = group.CurrentState();
    if (!state) return;
    bool enabled;
    int keep;
    uint64_t spans;
    bool revealAll;
    bool collapseActive;
    bool pointerOnBar;
    int hoveredStack;
    void* hoveredParent;
    std::vector<std::shared_ptr<PinnedItem>> siblings;
    {
        std::lock_guard lock(g_lock);
        auto config = ConfigForRoot(item->rootIdentity);
        enabled = config.hidePinned;
        keep = config.keepPinned;
        spans = config.stackRanges;
        revealAll = config.revealStacks;
        collapseActive = config.collapseStacks;
        pointerOnBar = config.pointerOnBar;
        hoveredStack = g_hoveredStack;
        hoveredParent = g_hoveredParent;
        if ((enabled && keep > 0) || spans)
            for (auto const& other : g_pinnedItems)
                if (other->parentIdentity == item->parentIdentity) siblings.push_back(other);
    }
    bool pinnedOnly = state.Name() == L"NoRunningIndicator";
    int pinnedBefore = 0;
    int position = 0;
    bool positionFound = false;
    if (siblings.size())
    {
        if (auto parent = Media::VisualTreeHelper::GetParent(button))
        {
            int count = Media::VisualTreeHelper::GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                auto child = Media::VisualTreeHelper::GetChild(parent, i).try_as<FrameworkElement>();
                if (!child) continue;
                if (child == button) { positionFound = true; break; }
                for (auto const& other : siblings)
                {
                    auto otherButton = other->button.get();
                    auto otherGroup = other->runningStates.get();
                    if (otherButton == child)
                    {
                        position++;
                        if (otherGroup && otherGroup.CurrentState() &&
                            otherGroup.CurrentState().Name() == L"NoRunningIndicator") pinnedBefore++;
                        break;
                    }
                }
            }
        }
    }
    int anchor = -1;
    int stackSize = 0;
    int stack = collapseActive && positionFound ? StackForPosition(position, spans, &anchor, &stackSize) : -1;
    item->stackIndex = stack;
    bool stackExpanded = revealAll || (pointerOnBar && stack >= 0 && stack == hoveredStack && item->parentIdentity == hoveredParent);
    bool stackAnchor = stack >= 0 && position == anchor;
    // A stack anchor stays visible even when the on-demand pinned-app setting is enabled.
    bool hideOnDemand = enabled && pinnedOnly && pinnedBefore >= keep && !stackAnchor && !stackExpanded;
    bool hideInStack = stack >= 0 && !stackAnchor && pinnedOnly && !stackExpanded;
    bool shouldHide = hideOnDemand || hideInStack;
    // The badge is part of the native icon panel, so it needs no separate window or timer.
    auto badge = item->stackBadge.get();
    if (!badge && stackAnchor)
    {
        auto iconPanel = item->iconPanel.get();
        if (iconPanel)
        {
            if (auto grid = iconPanel.try_as<winrt::Windows::UI::Xaml::Controls::Grid>())
            {
                using namespace winrt::Windows::UI::Xaml::Controls;
                using namespace winrt::Windows::UI::Xaml::Media;
                badge = Border{};
                badge.Width(15);
                badge.Height(15);
                badge.CornerRadius(CornerRadius{ 8, 8, 8, 8 });
                badge.Background(SolidColorBrush{ winrt::Windows::UI::Color{ 220, 38, 55, 79 } });
                badge.BorderBrush(SolidColorBrush{ winrt::Windows::UI::Color{ 180, 196, 219, 249 } });
                badge.BorderThickness(Thickness{ 1, 1, 1, 1 });
                badge.HorizontalAlignment(HorizontalAlignment::Right);
                badge.VerticalAlignment(VerticalAlignment::Top);
                badge.IsHitTestVisible(false);
                auto count = TextBlock{};
                count.FontSize(9);
                count.Foreground(SolidColorBrush{ winrt::Windows::UI::Color{ 255, 255, 255, 255 } });
                count.HorizontalAlignment(HorizontalAlignment::Center);
                count.VerticalAlignment(VerticalAlignment::Center);
                badge.Child(count);
                grid.Children().Append(badge);
                item->stackBadge = make_weak(badge);
            }
        }
    }
    if (badge)
    {
        badge.Visibility(stackAnchor && !stackExpanded ? Visibility::Visible : Visibility::Collapsed);
        if (stackAnchor) badge.Child().as<winrt::Windows::UI::Xaml::Controls::TextBlock>().Text(winrt::to_hstring(stackSize));
    }
    if (shouldHide && !item->hiddenByUs && button.Visibility() == Visibility::Visible)
    {
        item->hiddenByUs = true;
        button.Visibility(Visibility::Collapsed);
    }
    else if (!shouldHide && item->hiddenByUs)
    {
        item->hiddenByUs = false;
        button.Visibility(Visibility::Visible);
    }
}

static void RefreshTracked(void* root = nullptr)
{
    std::vector<Item> items;
    std::vector<std::shared_ptr<PinnedItem>> pinned;
    {
        std::lock_guard lock(g_lock);
        for (auto const& item : g_items)
            if (!root || item.rootIdentity == root) items.push_back(item);
        for (auto const& item : g_pinnedItems)
            if (!root || item->rootIdentity == root) pinned.push_back(item);
    }
    for (auto const& item : items)
        item.queue.TryEnqueue([item]
        {
            if (auto element = item.element.get())
            {
                bool hidden;
                { std::lock_guard lock(g_lock); hidden = IsHidden(item.bit, item.rootIdentity); }
                Apply(element, item.bit, hidden);
            }
        });
    for (auto const& item : pinned)
        item->queue.TryEnqueue([item] { ApplyPinned(item); });
}

static LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM w, LPARAM l)
{
    if (msg == WM_COPYDATA)
    {
        auto copy = reinterpret_cast<COPYDATASTRUCT const*>(l);
        if (!copy || copy->dwData != 0x57424D36 || !copy->lpData || copy->cbData < 8) return FALSE;
        auto bytes = static_cast<uint8_t const*>(copy->lpData);
        uint32_t version = 0, count = 0;
        std::memcpy(&version, bytes, 4);
        std::memcpy(&count, bytes + 4, 4);
        if (version != 1 || count > 8 || copy->cbData != 8 + count * sizeof(MonitorWireEntry)) return FALSE;
        std::vector<MonitorConfig> parsed;
        parsed.reserve(count);
        for (uint32_t i = 0; i < count; i++)
        {
            MonitorWireEntry entry{};
            std::memcpy(&entry, bytes + 8 + i * sizeof(entry), sizeof(entry));
            auto bar = reinterpret_cast<HWND>(static_cast<uintptr_t>(entry.taskbarHwnd));
            wchar_t name[64]{};
            if (!IsWindow(bar) || !GetClassNameW(bar, name, 64) ||
                (wcscmp(name, L"Shell_TrayWnd") && wcscmp(name, L"Shell_SecondaryTrayWnd")) ||
                entry.reserved != 0 || (entry.flags & ~31u) != 0 || (entry.trayMask & ~31u) != 0 ||
                entry.keepPinned > 10) return FALSE;
            for (auto const& existing : parsed) if (existing.taskbar == bar) return FALSE;
            MonitorConfig config;
            config.taskbar = bar;
            config.stackRanges = SanitizeSpans(entry.stackRanges);
            config.trayMask = static_cast<int>(entry.trayMask);
            config.hideBackground = (entry.flags & 1) != 0;
            config.hidePinned = (entry.flags & 2) != 0;
            config.revealStacks = (entry.flags & 4) != 0;
            config.pointerOnBar = (entry.flags & 8) != 0;
            config.collapseStacks = (entry.flags & 16) != 0;
            config.keepPinned = static_cast<int>(entry.keepPinned);
            parsed.push_back(config);
        }
        HWND leaveReceiver = nullptr;
        int leaveStack = -1;
        {
            std::lock_guard lock(g_lock);
            g_monitorConfigs.swap(parsed);
            if (g_hoveredStack >= 0)
            {
                auto effective = ConfigForRoot(g_hoveredRoot);
                if (count == 0 || !effective.pointerOnBar || !effective.collapseStacks || !effective.stackRanges)
                {
                    leaveReceiver = g_hoverReceiver;
                    leaveStack = g_hoveredStack;
                    g_hoveredStack = -1;
                    g_hoveredParent = nullptr;
                    g_hoveredRoot = nullptr;
                }
            }
        }
        if (leaveReceiver && leaveStack >= 0) PostMessageW(leaveReceiver, WM_APP + 5, leaveStack, 0);
        RefreshTracked();
        return TRUE;
    }
    if (msg == WM_APP + 4)
    {
        std::lock_guard lock(g_lock);
        auto receiver = reinterpret_cast<HWND>(w);
        g_hoverReceiver = IsWindow(receiver) ? receiver : nullptr;
        return g_hoverReceiver ? 1 : 0;
    }
    if (msg == WM_APP + 2 || msg == WM_APP + 3)
    {
        std::vector<std::shared_ptr<PinnedItem>> items;
        HWND leaveReceiver = nullptr;
        int leaveStack = -1;
        {
            std::lock_guard lock(g_lock);
            if (msg == WM_APP + 2)
            {
                g_pinnedHidden = (w & 1) != 0;
                g_keepPinned = static_cast<int>(w >> 1);
                if (g_keepPinned > 10) g_keepPinned = 10;
            }
            else
            {
                uint64_t requested = static_cast<uint64_t>(w);
                uint64_t accepted = 0;
                bool occupied[32]{};
                for (int group = 0; group < 4; group++)
                {
                    auto span = static_cast<int>((requested >> (group * 10)) & 0x3ff);
                    int start = span & 31;
                    int count = (span >> 5) & 31;
                    if (count < 2 || start + count > 32) continue;
                    bool overlaps = false;
                    for (int i = start; i < start + count; i++) if (occupied[i]) overlaps = true;
                    if (overlaps) continue;
                    for (int i = start; i < start + count; i++) occupied[i] = true;
                    accepted |= static_cast<uint64_t>(span) << (group * 10);
                }
                bool collapseActive = (l & 4) != 0;
                if (g_monitorConfigs.empty() && (accepted != g_stackSpans || collapseActive != g_stackCollapseActive || (l & 2) == 0))
                {
                    leaveReceiver = g_hoverReceiver;
                    leaveStack = g_hoveredStack;
                    g_hoveredStack = -1;
                    g_hoveredParent = nullptr;
                    g_hoveredRoot = nullptr;
                }
                g_stackSpans = accepted;
                g_stackRevealAll = (l & 1) != 0;
                g_stackCollapseActive = collapseActive;
            }
            items = g_pinnedItems;
        }
        if (leaveReceiver && leaveStack >= 0) PostMessageW(leaveReceiver, WM_APP + 5, leaveStack, 0);
        for (auto const& item : items)
            item->queue.TryEnqueue([item] { ApplyPinned(item); });
        return items.size();
    }
    if (msg != WM_APP && msg != WM_APP + 1) return DefWindowProcW(h, msg, w, l);
    std::vector<Item> items;
    {
        std::lock_guard lock(g_lock);
        if (msg == WM_APP) g_hidden = w != 0;
        else g_mask = static_cast<int>(w);
        for (auto const& item : g_items)
            if ((item.bit == 0) == (msg == WM_APP)) items.push_back(item);
    }
    for (auto const& item : items)
        item.queue.TryEnqueue([item]
        {
            if (auto element = item.element.get())
            {
                bool hidden;
                { std::lock_guard lock(g_lock); hidden = IsHidden(item.bit, item.rootIdentity); }
                Apply(element, item.bit, hidden);
            }
        });
    return items.size();
}

static void CreateControlWindow()
{
    if (g_wnd) return;
    auto inst = reinterpret_cast<HINSTANCE>(&__ImageBase);
    WNDCLASSW wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.lpszClassName = L"TaskbarTweakerTap6";
    RegisterClassW(&wc);
    g_wnd = CreateWindowExW(0, wc.lpszClassName, nullptr, 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, inst, nullptr);
}

// Matches Taskbar.TaskbarBackground > Grid > Rectangle#BackgroundFill / #BackgroundStroke.
static bool InTaskbarBackground(DependencyObject e)
{
    for (int i = 0; i < 4 && e; i++)
    {
        e = Media::VisualTreeHelper::GetParent(e);
        if (e && get_class_name(e) == L"Taskbar.TaskbarBackground") return true;
    }
    return false;
}

struct Tap : implements<Tap, IObjectWithSite, IVisualTreeServiceCallback2, non_agile>
{
    com_ptr<IUnknown> site;
    com_ptr<IXamlDiagnostics> diag;

    void TrackPinned(VisualElement const& el)
    {
        com_ptr<::IInspectable> obj;
        if (FAILED(diag->GetIInspectableFromHandle(el.Handle, obj.put()))) return;
        FrameworkElement panel{ nullptr };
        if (FAILED(obj->QueryInterface(guid_of<FrameworkElement>(), put_abi(panel)))) return;
        FrameworkElement button{ nullptr };
        DependencyObject ancestor = panel;
        for (int i = 0; i < 6 && ancestor; i++)
        {
            ancestor = Media::VisualTreeHelper::GetParent(ancestor);
            if (ancestor && (get_class_name(ancestor) == L"Taskbar.TaskListButton" ||
                             get_class_name(ancestor) == L"Taskbar.TaskListLabeledButton"))
            {
                button = ancestor.try_as<FrameworkElement>();
                break;
            }
        }
        if (!button) return;
        for (auto const& group : VisualStateManager::GetVisualStateGroups(panel))
        {
            if (group.Name() != L"RunningIndicatorStates") continue;
            auto item = std::make_shared<PinnedItem>();
            item->handle = el.Handle;
            item->button = make_weak(button);
            item->iconPanel = make_weak(panel);
            item->runningStates = make_weak(group);
            item->parentIdentity = get_abi(Media::VisualTreeHelper::GetParent(button));
            item->queue = winrt::Windows::System::DispatcherQueue::GetForCurrentThread();
            item->rootIdentity = RootIdentity(button);
            auto owner = OwnerFromElement(button);
            {
                std::lock_guard lock(g_lock);
                for (auto const& existing : g_pinnedItems) if (existing->handle == item->handle) return;
                g_pinnedItems.push_back(item);
            }
            if (RememberOwner(item->rootIdentity, owner)) RefreshTracked(item->rootIdentity);
            std::weak_ptr<PinnedItem> weak = item;
            group.CurrentStateChanged([weak](IInspectable const&, VisualStateChangedEventArgs const&)
            {
                if (auto current = weak.lock()) ApplyPinned(current);
            });
            button.PointerEntered([weak](IInspectable const&, Input::PointerRoutedEventArgs const&)
            {
                auto current = weak.lock();
                if (!current || current->stackIndex < 0) return;
                std::vector<std::shared_ptr<PinnedItem>> items;
                HWND receiver = nullptr;
                HWND previousReceiver = nullptr;
                int previousStack = -1;
                {
                    std::lock_guard lock(g_lock);
                    if (g_hoveredStack == current->stackIndex && g_hoveredParent == current->parentIdentity) return;
                    if (g_hoveredStack >= 0) { previousStack = g_hoveredStack; previousReceiver = g_hoverReceiver; }
                    g_hoveredStack = current->stackIndex;
                    g_hoveredParent = current->parentIdentity;
                    g_hoveredRoot = current->rootIdentity;
                    receiver = g_hoverReceiver;
                    items = g_pinnedItems;
                }
                if (previousReceiver) PostMessageW(previousReceiver, WM_APP + 5, previousStack, 0);
                if (receiver) PostMessageW(receiver, WM_APP + 5, current->stackIndex, 1);
                for (auto const& other : items)
                    other->queue.TryEnqueue([other] { ApplyPinned(other); });
            });
            button.PointerExited([weak](IInspectable const&, Input::PointerRoutedEventArgs const&)
            {
                auto current = weak.lock();
                if (!current || current->stackIndex < 0) return;
                HWND receiver = nullptr;
                {
                    std::lock_guard lock(g_lock);
                    if (g_hoveredStack == current->stackIndex && g_hoveredParent == current->parentIdentity)
                        receiver = g_hoverReceiver;
                }
                if (receiver) PostMessageW(receiver, WM_APP + 5, current->stackIndex, 0);
            });
            ApplyPinned(item);
            return;
        }
    }

    HRESULT __stdcall SetSite(IUnknown* s) noexcept override
    {
        site = nullptr;
        diag = nullptr;
        if (!s) return S_OK;
        site.copy_from(s);
        diag = site.try_as<IXamlDiagnostics>();
        auto tree = site.try_as<IVisualTreeService3>();
        if (!diag || !tree) return E_NOINTERFACE;
        CreateControlWindow();
        // AdviseVisualTreeChange walks the tree on the UI thread and waits for it; calling it here would deadlock.
        std::thread([tree, self = get_strong()] { tree->AdviseVisualTreeChange(self.get()); }).detach();
        return S_OK;
    }

    HRESULT __stdcall GetSite(REFIID iid, void** out) noexcept override
    {
        *out = nullptr;
        return site ? site->QueryInterface(iid, out) : E_FAIL;
    }

    HRESULT __stdcall OnVisualTreeChange(ParentChildRelation, VisualElement el, VisualMutationType type) noexcept override
    {
        try
        {
            if (type == Remove)
            {
                std::vector<std::shared_ptr<PinnedItem>> removed;
                {
                    std::lock_guard lock(g_lock);
                    std::erase_if(g_items, [&](Item const& it) { return it.handle == el.Handle; });
                    std::erase_if(g_pinnedItems, [&](auto const& it)
                    {
                        if (it->handle != el.Handle) return false;
                        removed.push_back(it);
                        return true;
                    });
                }
                for (auto const& item : removed)
                    if (item->hiddenByUs) if (auto button = item->button.get()) button.Visibility(Visibility::Visible);
                return S_OK;
            }
            const wchar_t* name = el.Name ? el.Name : L"";
            const wchar_t* type = el.Type ? el.Type : L"";
            if (!wcscmp(name, L"IconPanel")) TrackPinned(el);
            int bit = -1;
            if (!wcscmp(name, L"BackgroundFill") || !wcscmp(name, L"BackgroundStroke")) bit = 0;
            else for (auto& p : kParts) if (!wcscmp(type, p.type) && !wcscmp(name, p.name)) bit = p.bit;
            if (bit < 0) return S_OK;

            com_ptr<::IInspectable> obj;
            if (FAILED(diag->GetIInspectableFromHandle(el.Handle, obj.put()))) return S_OK;
            UIElement e{ nullptr };
            if (FAILED(obj->QueryInterface(guid_of<UIElement>(), put_abi(e))) || (bit == 0 && !InTaskbarBackground(e))) return S_OK;

            void* root = RootIdentity(e);
            auto owner = OwnerFromElement(e);

            // Explorer resets opacity/visibility on theme or state changes; force it back while hidden.
            e.RegisterPropertyChangedCallback(bit == 0 ? UIElement::OpacityProperty() : UIElement::VisibilityProperty(),
                [bit, root](DependencyObject const& o, DependencyProperty const&)
            {
                bool hidden;
                { std::lock_guard lock(g_lock); hidden = IsHidden(bit, root); }
                if (!hidden) return;
                auto u = o.as<UIElement>();
                if (bit == 0 ? u.Opacity() != 0.0 : u.Visibility() != Visibility::Collapsed) Apply(u, bit, true);
            });

            bool hidden;
            {
                std::lock_guard lock(g_lock);
                g_items.push_back({ el.Handle, make_weak(e), winrt::Windows::System::DispatcherQueue::GetForCurrentThread(), bit, root });
            }
            if (RememberOwner(root, owner)) RefreshTracked(root);
            { std::lock_guard lock(g_lock); hidden = IsHidden(bit, root); }
            // Tray parts are only touched when hidden, so explorer's own visibility stays as is otherwise.
            if (bit == 0 || hidden) Apply(e, bit, hidden);
        }
        catch (...) {}
        return S_OK;
    }

    HRESULT __stdcall OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) noexcept override { return S_OK; }
};

struct Factory : implements<Factory, IClassFactory>
{
    HRESULT __stdcall CreateInstance(IUnknown* outer, REFIID iid, void** out) noexcept override
    {
        *out = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        try { return make_self<Tap>()->QueryInterface(iid, out); }
        catch (...) { return to_hresult(); }
    }

    HRESULT __stdcall LockServer(BOOL) noexcept override { return S_OK; }
};

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, LPVOID* out)
{
    *out = nullptr;
    if (clsid != CLSID_Tap) return CLASS_E_CLASSNOTAVAILABLE;
    try { return make_self<Factory>()->QueryInterface(iid, out); }
    catch (...) { return to_hresult(); }
}

// Stay loaded: XAML holds callbacks into this DLL until explorer exits.
STDAPI DllCanUnloadNow() { return S_FALSE; }
