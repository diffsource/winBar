// XAML diagnostics TAP that TaskbarTweaker loads into explorer.exe via InitializeXamlDiagnosticsEx.
// Windows 11 draws the taskbar background in XAML on top of the window, which hides any accent set with
// SetWindowCompositionAttribute. This DLL fades out that XAML background so the accent shows through.
// It can also collapse parts of the system tray (clock, quick settings, ...).
// Control: message-only window "TaskbarTweakerTap4".
//   WM_APP:     wParam 1 = hide background, 0 = restore. Reply: number of background elements found.
//   WM_APP + 1: wParam = bit mask of kParts to hide. Reply: number of tray elements found.
//   WM_APP + 2: wParam bit 0 = hide pinned-only buttons, high bits = first N pinned buttons to keep.
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
#include <memory>
#include <mutex>
#include <thread>
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
};

struct PinnedItem
{
    InstanceHandle handle; // IconPanel visual-tree handle
    weak_ref<FrameworkElement> button;
    weak_ref<VisualStateGroup> runningStates;
    void* parentIdentity = nullptr; // UI-thread parent identity, used only for sibling ordering
    winrt::Windows::System::DispatcherQueue queue{ nullptr };
    bool hiddenByUs = false; // touched only on the button's UI thread
};

static std::mutex g_lock;
static std::vector<Item> g_items;
static std::vector<std::shared_ptr<PinnedItem>> g_pinnedItems;
static bool g_hidden = true;
static int g_mask;
static bool g_pinnedHidden;
static int g_keepPinned;
static HWND g_wnd;

static bool IsHidden(int bit) { return bit == 0 ? g_hidden : (g_mask & bit) != 0; } // caller holds g_lock

// Runs on the element's UI thread.
// shortcut: un-hiding sets Visible instead of restoring explorer's own value; restart explorer if a part looks wrong.
static void Apply(UIElement const& e, int bit, bool hidden)
{
    if (bit == 0) e.Opacity(hidden ? 0.0 : 1.0);
    else e.Visibility(hidden ? Visibility::Collapsed : Visibility::Visible);
}

// The taskbar's RunningIndicatorStates group reports NoRunningIndicator for pinned apps with no
// running window. Unknown states stay visible so a Windows update cannot hide running apps by default.
static void ApplyPinned(std::shared_ptr<PinnedItem> const& item)
{
    auto button = item->button.get();
    auto group = item->runningStates.get();
    if (!button || !group) return;
    auto state = group.CurrentState();
    if (!state) return;
    bool enabled;
    int keep;
    std::vector<std::shared_ptr<PinnedItem>> siblings;
    {
        std::lock_guard lock(g_lock);
        enabled = g_pinnedHidden;
        keep = g_keepPinned;
        if (enabled && keep > 0)
            for (auto const& other : g_pinnedItems)
                if (other->parentIdentity == item->parentIdentity) siblings.push_back(other);
    }
    bool pinnedOnly = state.Name() == L"NoRunningIndicator";
    int pinnedBefore = 0;
    if (enabled && pinnedOnly && keep > 0)
    {
        if (auto parent = Media::VisualTreeHelper::GetParent(button))
        {
            int count = Media::VisualTreeHelper::GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                auto child = Media::VisualTreeHelper::GetChild(parent, i).try_as<FrameworkElement>();
                if (!child) continue;
                if (child == button) break;
                for (auto const& other : siblings)
                {
                    auto otherButton = other->button.get();
                    auto otherGroup = other->runningStates.get();
                    if (otherButton == child && otherGroup && otherGroup.CurrentState() &&
                        otherGroup.CurrentState().Name() == L"NoRunningIndicator")
                    {
                        pinnedBefore++;
                        break;
                    }
                }
            }
        }
    }
    bool shouldHide = enabled && pinnedOnly && pinnedBefore >= keep;
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

static LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM w, LPARAM l)
{
    if (msg == WM_APP + 2)
    {
        std::vector<std::shared_ptr<PinnedItem>> items;
        {
            std::lock_guard lock(g_lock);
            g_pinnedHidden = (w & 1) != 0;
            g_keepPinned = static_cast<int>(w >> 1);
            if (g_keepPinned > 10) g_keepPinned = 10;
            items = g_pinnedItems;
        }
        for (auto const& item : items)
            item->queue.TryEnqueue([item] { ApplyPinned(item); });
        return items.size();
    }
    if (msg != WM_APP && msg != WM_APP + 1) return DefWindowProcW(h, msg, w, l);
    std::lock_guard lock(g_lock);
    int changed = 0; // bit 0 of "changed" stands for the background here, tray bits shifted up by one
    if (msg == WM_APP)
    {
        bool hidden = w != 0;
        if (hidden != g_hidden) { g_hidden = hidden; changed = 1; }
    }
    else
    {
        int mask = static_cast<int>(w);
        changed = (mask ^ g_mask) << 1;
        g_mask = mask;
    }
    size_t found = 0;
    for (auto& it : g_items)
    {
        if ((it.bit == 0) != (msg == WM_APP)) continue;
        found++;
        if (changed & (it.bit == 0 ? 1 : it.bit << 1))
            it.queue.TryEnqueue([el = it.element, bit = it.bit, hidden = IsHidden(it.bit)] { if (auto e = el.get()) Apply(e, bit, hidden); });
    }
    return found;
}

static void CreateControlWindow()
{
    if (g_wnd) return;
    auto inst = reinterpret_cast<HINSTANCE>(&__ImageBase);
    WNDCLASSW wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.lpszClassName = L"TaskbarTweakerTap4";
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
            item->runningStates = make_weak(group);
            item->parentIdentity = get_abi(Media::VisualTreeHelper::GetParent(button));
            item->queue = winrt::Windows::System::DispatcherQueue::GetForCurrentThread();
            {
                std::lock_guard lock(g_lock);
                for (auto const& existing : g_pinnedItems) if (existing->handle == item->handle) return;
                g_pinnedItems.push_back(item);
            }
            std::weak_ptr<PinnedItem> weak = item;
            group.CurrentStateChanged([weak](IInspectable const&, VisualStateChangedEventArgs const&)
            {
                if (auto current = weak.lock()) ApplyPinned(current);
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

            // Explorer resets opacity/visibility on theme or state changes; force it back while hidden.
            e.RegisterPropertyChangedCallback(bit == 0 ? UIElement::OpacityProperty() : UIElement::VisibilityProperty(),
                [bit](DependencyObject const& o, DependencyProperty const&)
            {
                bool hidden;
                { std::lock_guard lock(g_lock); hidden = IsHidden(bit); }
                if (!hidden) return;
                auto u = o.as<UIElement>();
                if (bit == 0 ? u.Opacity() != 0.0 : u.Visibility() != Visibility::Collapsed) Apply(u, bit, true);
            });

            std::lock_guard lock(g_lock);
            g_items.push_back({ el.Handle, make_weak(e), winrt::Windows::System::DispatcherQueue::GetForCurrentThread(), bit });
            // Tray parts are only touched when hidden, so explorer's own visibility stays as is otherwise.
            if (bit == 0 || IsHidden(bit)) Apply(e, bit, IsHidden(bit));
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
