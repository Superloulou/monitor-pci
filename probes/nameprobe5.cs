using System;
using System.Runtime.InteropServices;
using System.Text;

public static class NP4
{
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid guid;
        public uint pid;
        public DEVPROPKEY(Guid g, uint p) { guid = g; pid = p; }
    }

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Locate_DevNodeW(out uint di, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Child(out uint child, uint di, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Sibling(out uint sib, uint di, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint di, StringBuilder buf, int len, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_PropertyW(uint di, ref DEVPROPKEY key, out uint type, IntPtr buf, ref uint size, uint flags);

    static Guid PciSet = new Guid("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62");
    static Guid DevSet = new Guid("A45C254E-DF1C-4ECD-8020-67D146A850E0");

    static string TryStr(uint di, Guid g, uint pid)
    {
        DEVPROPKEY k = new DEVPROPKEY(g, pid);
        uint type = 0, size = 0;
        int cr = CM_Get_DevNode_PropertyW(di, ref k, out type, IntPtr.Zero, ref size, 0);
        if (cr != 0x103 || size == 0 || size > 4096) return "(cr=" + cr + ")";
        IntPtr b = Marshal.AllocHGlobal((int)size);
        try
        {
            cr = CM_Get_DevNode_PropertyW(di, ref k, out type, b, ref size, 0);
            if (cr != 0) return "(cr=" + cr + ")";
            return Marshal.PtrToStringUni(b);
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    static string TryU32(uint di, Guid g, uint pid)
    {
        DEVPROPKEY k = new DEVPROPKEY(g, pid);
        uint type = 0, size = 4;
        IntPtr b = Marshal.AllocHGlobal(4);
        try
        {
            int cr = CM_Get_DevNode_PropertyW(di, ref k, out type, b, ref size, 0);
            if (cr != 0) return "(cr=" + cr + ")";
            return Marshal.ReadInt32(b).ToString();
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    static string IdOf(uint di)
    {
        StringBuilder sb = new StringBuilder(512);
        if (CM_Get_Device_IDW(di, sb, sb.Capacity, 0) != 0) return "?";
        return sb.ToString();
    }

    static int hits = 0;

    static void Walk(uint di, int depth)
    {
        if (depth > 20 || hits > 400) return;
        uint child;
        if (CM_Get_Child(out child, di, 0) != 0) return;
        do
        {
            string sp = TryU32(child, PciSet, 9);
            string w = TryU32(child, PciSet, 10);
            if (!sp.StartsWith("(") && !w.StartsWith("("))
            {
                hits++;
                Console.WriteLine("GEN" + sp + " x" + w + "  " + IdOf(child));
                Console.WriteLine("      pid2  DeviceDesc   = " + TryStr(child, DevSet, 2));
                Console.WriteLine("      pid14 FriendlyName = " + TryStr(child, DevSet, 14));
                Console.WriteLine("      pid4  Class        = " + TryStr(child, DevSet, 4));
                Console.WriteLine("      pid12 Name       = " + TryStr(child, DevSet, 12));
            }
            Walk(child, depth + 1);
        }
        while (CM_Get_Sibling(out child, child, 0) == 0);
    }

    public static void Run()
    {
        uint root;
        int cr = CM_Locate_DevNodeW(out root, null, 0);
        Console.WriteLine("root cr=" + cr + " id=" + (cr == 0 ? IdOf(root) : "-"));
        if (cr != 0) return;
        Walk(root, 0);
        Console.WriteLine("hits=" + hits);
    }
}
