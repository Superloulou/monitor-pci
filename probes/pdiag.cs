using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class PDiag
{
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    static extern int CM_Get_DevNode_Property(uint devInst, ref DEVPROPKEY propKey, out uint type, IntPtr buffer, ref uint size, int flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Child(out uint child, uint devInst, int flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Sibling(out uint sibling, uint devInst, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int length, int flags);

    static Guid DevSet = new Guid("78C34FC8-104A-4ACA-9EA4-524D52999E57");
    static Guid PciSet = new Guid("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62");

    static string TryStr(uint di, Guid set, uint pid)
    {
        DEVPROPKEY k = new DEVPROPKEY(); k.fmtid = set; k.pid = pid;
        uint t = 0, size = 0;
        int cr = CM_Get_DevNode_Property(di, ref k, out t, IntPtr.Zero, ref size, 0);
        if (cr != 0 && cr != 19 && cr != 13 && cr != 37 && cr != 117) return "<cr=" + cr + ">";
        if (size == 0 || size > 4096) return "<cr=" + cr + " size=" + size + ">";
        IntPtr b = Marshal.AllocHGlobal((int)size);
        try
        {
            uint t2 = 0; uint s2 = size;
            cr = CM_Get_DevNode_Property(di, ref k, out t2, b, ref s2, 0);
            if (cr != 0) return "<cr2=" + cr + ">";
            return Marshal.PtrToStringUni(b) + " <type=" + t2 + ">";
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    static string TryU32(uint di, Guid set, uint pid)
    {
        DEVPROPKEY k = new DEVPROPKEY(); k.fmtid = set; k.pid = pid;
        uint t = 0, size = 4;
        IntPtr b = Marshal.AllocHGlobal(4);
        try
        {
            int cr = CM_Get_DevNode_Property(di, ref k, out t, b, ref size, 0);
            if (cr != 0) return "cr=" + cr;
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

    public static string Run()
    {
        StringBuilder o = new StringBuilder();
        string[] ids = new string[] {
            @"PCI\VEN_15B7&DEV_5015&SUBSYS_501515B7&REV_01\4&3606B996&0&0000",
            @"PCI\VEN_17CB&DEV_1107&SUBSYS_110717CB&REV_01\4&CDACDEC&0&0000"
        };
        foreach (string id in ids)
        {
            uint di;
            int cr = CM_Locate_DevNodeW(out di, id, 0);
            o.Append("### " + id + " locate cr=" + cr + "\r\n");
            if (cr != 0) continue;
            uint[] pids = new uint[] { 2, 15, 16, 24, 4, 12 };
            foreach (uint p in pids)
                o.Append("   devpid " + p + " = " + TryStr(di, DevSet, p) + "\r\n");
            o.Append("   link cur speed/width = " + TryU32(di, PciSet, 9) + " / " + TryU32(di, PciSet, 10) +
                     "  max = " + TryU32(di, PciSet, 11) + " / " + TryU32(di, PciSet, 12) + "\r\n");
        }

        // Full tree walk: print every node that has link props, with depth, to find root ports.
        o.Append("\r\n=== TREE (nodes with PCIe link props) ===\r\n");
        uint root;
        if (CM_Locate_DevNodeW(out root, null, 0) != 0) { o.Append("no root"); return o.ToString(); }
        Walk(root, o, 0);
        return o.ToString();
    }

    static void Walk(uint di, StringBuilder o, int depth)
    {
        if (depth > 20) return;
        uint child;
        if (CM_Get_Child(out child, di, 0) != 0) return;
        do
        {
            string s = TryU32(child, PciSet, 9);
            string w = TryU32(child, PciSet, 10);
            string id = IdOf(child);
            if (s != null && !s.StartsWith("cr="))
            {
                o.Append(new string(' ', depth * 2) + id + " speed=" + s + " width=" + w +
                         " max=" + TryU32(child, PciSet, 11) + "/" + TryU32(child, PciSet, 12) +
                         " name=" + TryStr(child, DevSet, 2) + "\r\n");
            }
            else if (id.Contains("PNP0A08") || id.Contains("VEN_17CB&DEV_0111"))
            {
                o.Append(new string(' ', depth * 2) + "[no-link] " + id + " speed=" + s + "\r\n");
            }
            Walk(child, o, depth + 1);
            uint sib;
            if (CM_Get_Sibling(out sib, child, 0) != 0) break;
            child = sib;
        } while (true);
    }
}
