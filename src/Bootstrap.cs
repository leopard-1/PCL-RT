// Bootstrap: dnlib.dll is embedded as a managed resource so PCL-Rebuilder ships as a
// single file. Nothing is written to disk; the assembly is loaded from memory on demand.
using System;
using System.IO;
using System.Reflection;

internal static class Bootstrap
{
    private static Assembly _resolver;

    [STAThread]
    private static int Main(string[] args)
    {
        // Register before anything touches dnlib types.
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        return PCLRebuilder.Run(args);
    }

    private static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string simpleName = new AssemblyName(e.Name).Name;
        if (!simpleName.Equals("dnlib", StringComparison.OrdinalIgnoreCase)) return null;
        if (_resolver != null) return _resolver;

        Assembly self = Assembly.GetExecutingAssembly();
        // Resource name is "<RootNamespace>.<file>" - we pass "dnlib.dll" at compile time,
        // so the manifest name ends up as "dnlib.dll" (no namespace prefix).
        foreach (string name in self.GetManifestResourceNames())
        {
            if (!name.EndsWith("dnlib.dll", StringComparison.OrdinalIgnoreCase)) continue;
            using (Stream s = self.GetManifestResourceStream(name))
            {
                if (s == null) continue;
                byte[] buf = new byte[s.Length];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = s.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                _resolver = Assembly.Load(buf);
                return _resolver;
            }
        }
        return null;
    }
}
