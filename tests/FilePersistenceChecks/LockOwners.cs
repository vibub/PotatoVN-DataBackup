using System.Runtime.InteropServices;
using System.Text;

internal static class LockOwners
{
    [StructLayout(LayoutKind.Sequential)] private struct ProcessId { public uint Id; public System.Runtime.InteropServices.ComTypes.FILETIME Started; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessInfo
    {
        public ProcessId Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public int Kind; public uint Status; public uint Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint count, string[] names, uint processes, IntPtr process, uint services, IntPtr serviceNames);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? entries, ref uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
    public static void Dump(string file)
    {
        Console.Error.WriteLine($"Target exists={File.Exists(file)} attributes={(File.Exists(file) ? File.GetAttributes(file) : 0)}");
        int result = RmStartSession(out uint session, 0, new StringBuilder(33));
        if (result != 0) { Console.Error.WriteLine("RmStartSession=" + result); return; }
        try
        {
            result = RmRegisterResources(session, 1, [file], 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (result != 0) { Console.Error.WriteLine("RmRegisterResources=" + result); return; }
            uint count = 0, reasons = 0;
            result = RmGetList(session, out uint needed, ref count, null, ref reasons);
            if (result == 234)
            {
                ProcessInfo[] list = new ProcessInfo[needed]; count = needed;
                result = RmGetList(session, out _, ref count, list, ref reasons);
                foreach (ProcessInfo item in list.Take((int)count)) Console.Error.WriteLine($"Lock owner pid={item.Process.Id} name={item.Name} service={item.Service}");
            }
            Console.Error.WriteLine($"RmGetList={result}, owners={count}");
        }
        finally { RmEndSession(session); }
    }
}
