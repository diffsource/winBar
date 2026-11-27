// XAML diagnostics TAP that TaskbarTweaker loads into explorer.exe via InitializeXamlDiagnosticsEx.
// Windows 11 draws the taskbar background in XAML on top of the window, which hides any accent set with
// SetWindowCompositionAttribute. This DLL fades out that XAML background so the accent shows through.
// Control: message-only window "TaskbarTweakerTap", WM_APP with wParam 1 = hide, 0 = restore.
// The reply is the number of background elements found (0 means the XAML tree did not match).
#include <windows.h>
#undef GetCurrentTime // clashes with Storyboard::GetCurrentTime in C++/WinRT
#include <unknwn.h>
#include <ocidl.h>
#include <xamlOM.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Media.h>
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

struct Item
{
    InstanceHandle handle;
    weak_ref<UIElement> element;
    winrt::Windows::System::DispatcherQueue queue{ nullptr };
};

static std::mutex g_lock;
static std::vector<Item> g_items;
static bool g_hidden = true;
static HWND g_wnd;

// Runs on the element's UI thread.
static void Apply(UIElement const& e, bool hidden)
{
    e.Opacity(hidden ? 0.0 : 1.0);
}

static LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM w, LPARAM l)
{
    if (msg != WM_APP) return DefWindowProcW(h, msg, w, l);
    std::lock_guard lock(g_lock);
    bool hidden = w != 0;
    if (hidden != g_hidden)
    {
        g_hidden = hidden;
        for (auto& it : g_items)
            it.queue.TryEnqueue([el = it.element, hidden] { if (auto e = el.get()) Apply(e, hidden); });
    }
    return g_items.size();
}

static void CreateControlWindow()
{
    if (g_wnd) return;
    auto inst = reinterpret_cast<HINSTANCE>(&__ImageBase);
    WNDCLASSW wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.lpszClassName = L"TaskbarTweakerTap";
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
                std::lock_guard lock(g_lock);
                std::erase_if(g_items, [&](Item const& it) { return it.handle == el.Handle; });
                return S_OK;
            }
            if (!el.Name || (wcscmp(el.Name, L"BackgroundFill") != 0 && wcscmp(el.Name, L"BackgroundStroke") != 0)) return S_OK;

            com_ptr<::IInspectable> obj;
            if (FAILED(diag->GetIInspectableFromHandle(el.Handle, obj.put()))) return S_OK;
            UIElement e{ nullptr };
            if (FAILED(obj->QueryInterface(guid_of<UIElement>(), put_abi(e))) || !InTaskbarBackground(e)) return S_OK;

            // Explorer resets the opacity on theme or state changes; force it back while hidden.
            e.RegisterPropertyChangedCallback(UIElement::OpacityProperty(), [](DependencyObject const& o, DependencyProperty const&)
            {
                bool hidden;
                { std::lock_guard lock(g_lock); hidden = g_hidden; }
                auto u = o.as<UIElement>();
                if (hidden && u.Opacity() != 0.0) u.Opacity(0.0);
            });

            std::lock_guard lock(g_lock);
            g_items.push_back({ el.Handle, make_weak(e), winrt::Windows::System::DispatcherQueue::GetForCurrentThread() });
            Apply(e, g_hidden);
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
