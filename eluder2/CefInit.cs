// CefInit.cs — Fully reflection-based CEF init
// CefSharp.Core is C++/CLI and cannot be referenced at Roslyn compile time in net8.0.
// We load it at runtime and call Cef.Initialize / Cef.Shutdown via reflection.
using System;
using System.IO;
using System.Reflection;

namespace Eluder2
{
    internal static class CefInit
    {
        private static Assembly? _coreAsm;
        private static Type?     _cefType;

        private static Assembly GetCore()
        {
            if (_coreAsm != null) return _coreAsm;

            // Try already-loaded first (e.g. CefSharp.WinForms pulled it in)
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "CefSharp.Core") { _coreAsm = a; break; }
            }

            // Otherwise load from exe directory
            if (_coreAsm == null)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "CefSharp.Core.dll");
                if (!File.Exists(path))
                    throw new FileNotFoundException("CefSharp.Core.dll not found next to exe.", path);
                _coreAsm = Assembly.LoadFrom(path);
            }
            return _coreAsm;
        }

        public static void Initialize()
        {
            var core = GetCore();

            // ── Build CefSettings ────────────────────────────────────────────────
            Type? settingsType = core.GetType("CefSharp.CefSettings");
            if (settingsType == null)
                throw new InvalidOperationException("CefSharp.CefSettings not found in CefSharp.Core.dll");

            object settings = Activator.CreateInstance(settingsType)!;

            SetProp(settings, "CachePath", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Eluder", "CEFCache"));

            // LogSeverity.Disable = 99 (enum value)
            var logSevType = settingsType.Assembly.GetType("CefSharp.LogSeverity")
                          ?? GetCore().GetType("CefSharp.LogSeverity");
            if (logSevType != null)
                SetProp(settings, "LogSeverity", Enum.ToObject(logSevType, 99));

            SetProp(settings, "BrowserSubprocessPath",
                System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName);

            SetProp(settings, "RemoteDebuggingPort", 0);

            // CefCommandLineArgs is a Dictionary-like object
            var cmdArgs = GetProp(settings, "CefCommandLineArgs");
            if (cmdArgs != null)
            {
                var indexer = cmdArgs.GetType().GetProperty("Item", new[] { typeof(string) });
                void SetArg(string k, string v) => indexer?.SetValue(cmdArgs, v, new object[] { k });
                SetArg("disable-gpu-vsync",                      "1");
                SetArg("disable-backgrounding-occluded-windows", "1");
                SetArg("disable-renderer-backgrounding",         "1");
                SetArg("disable-background-timer-throttling",    "1");
                SetArg("process-per-site",                       "1");
            }

            // ── Cef.Initialize ───────────────────────────────────────────────────
            _cefType = core.GetType("CefSharp.Cef");
            if (_cefType == null)
                throw new InvalidOperationException("CefSharp.Cef not found in CefSharp.Core.dll");

            // Find Initialize(CefSettingsBase, bool, IBrowserProcessHandler)
            MethodInfo? initMethod = null;
            foreach (var m in _cefType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Initialize") continue;
                var p = m.GetParameters();
                if (p.Length == 3) { initMethod = m; break; }
            }
            initMethod ??= _cefType.GetMethod("Initialize",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { settings.GetType().BaseType ?? settings.GetType(), typeof(bool), typeof(object) }, null);

            if (initMethod == null)
                throw new InvalidOperationException("Cef.Initialize(settings, bool, handler) not found.");

            initMethod.Invoke(null, new object?[] { settings, false, null });
        }

        public static void Shutdown()
        {
            try
            {
                _cefType ??= GetCore().GetType("CefSharp.Cef");
                _cefType?.GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static)
                         ?.Invoke(null, null);
            }
            catch { }
        }

        private static void SetProp(object obj, string name, object? value)
        {
            obj.GetType().GetProperty(name)?.SetValue(obj, value);
        }

        private static object? GetProp(object obj, string name)
        {
            return obj.GetType().GetProperty(name)?.GetValue(obj);
        }
    }
}
