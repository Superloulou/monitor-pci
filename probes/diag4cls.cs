using System;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

public class Diag4
{
    [StructLayout(LayoutKind.Sequential)]
    struct TEXDESC
    {
        public int Width; public int Height; public int MipLevels; public int ArraySize;
        public int Format; public int SampleCount; public int SampleQuality;
        public int Usage; public int BindFlags; public int CPUAccessFlags;
        public int MiscFlags; public int NodeMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MAPPED
    {
        public IntPtr pData; public uint RowPitch; public uint DepthPitch;
    }

    delegate int CreateTexDel(IntPtr self, IntPtr pDesc, IntPtr pInit, out IntPtr ppTex);
    delegate void CopyResDel(IntPtr self, IntPtr dst, IntPtr src);
    delegate void FlushDel(IntPtr self);
    delegate void UpdateSubDel(IntPtr self, IntPtr dst, int dstSub, IntPtr pBox, IntPtr pData, int rowPitch, int depthPitch);
    delegate int MapDel(IntPtr self, IntPtr res, int subres, int mapType, uint flags, out MAPPED pMap);
    delegate void UnmapDel(IntPtr self, IntPtr res, int subres);
    delegate int GetRemovedDel(IntPtr self);
    delegate int GetFLDel(IntPtr self);

    [DllImport("d3d11.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern int D3D11CreateDevice(IntPtr pAdapter, int driverType, IntPtr hSoftware, uint flags, IntPtr pLevels, uint nLevels, uint sdk, out IntPtr ppDev, out int pLevel, out IntPtr ppCtx);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    static extern void CopyMemory(IntPtr dst, IntPtr src, UIntPtr len);

    static IntPtr Slot(IntPtr o, int i) { IntPtr vt = Marshal.ReadIntPtr(o); return Marshal.ReadIntPtr(vt, i * IntPtr.Size); }
    static IntPtr Off(IntPtr p, long o) { return new IntPtr(p.ToInt64() + o); }
    static string H(int v) { return "0x" + v.ToString("X8"); }
    static string Gb(long bytes, double ms) { if (ms <= 0.0) return "n/a"; return ((double)bytes / (ms / 1000.0) / 1073741824.0).ToString("0.00"); }

    static StringBuilder sb = new StringBuilder();
    static int lvlOut;

    static double[] Times(int reps, Action a)
    {
        double[] t = new double[reps];
        Stopwatch sw = new Stopwatch();
        for (int i = 0; i < reps; i++)
        {
            sw.Restart();
            a();
            sw.Stop();
            t[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(t);
        return t;
    }

    static string Stat(double[] t)
    {
        if (t == null || t.Length == 0) return "n/a";
        return "min " + t[0].ToString("0.000") + " ms / med " + t[t.Length / 2].ToString("0.000") + " ms / max " + t[t.Length - 1].ToString("0.000") + " ms";
    }

    public static string Run()
    {
        sb.Length = 0;
        foreach (System.Diagnostics.ProcessModule m in Process.GetCurrentProcess().Modules)
        {
            string n = m.ModuleName.ToLower();
            if (n.IndexOf("qcom") >= 0 || n.IndexOf("adreno") >= 0 || n.IndexOf("warp") >= 0 || n.IndexOf("d3d11") >= 0 || n.IndexOf("dxgi") >= 0 || n.IndexOf("basic") >= 0)
                sb.AppendLine("mod " + m.ModuleName + " @ " + m.BaseAddress);
        }

        foreach (int dt in new int[] { 1, 5 })
        {
            string label = (dt == 1) ? "HARDWARE" : "WARP";
            IntPtr dev, dc;
            int hr = D3D11CreateDevice(IntPtr.Zero, dt, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out dev, out lvlOut, out dc);
            sb.AppendLine("== " + label + " create hr=" + H(hr) + " lvl=" + H(lvlOut) + " dev=" + dev.ToString("X") + " dc=" + dc.ToString("X"));
            if (hr != 0 || dev == IntPtr.Zero || dc == IntPtr.Zero) continue;

            CreateTexDel CreateTex = (CreateTexDel)Marshal.GetDelegateForFunctionPointer(Slot(dev, 5), typeof(CreateTexDel));
            CopyResDel CopyRes = (CopyResDel)Marshal.GetDelegateForFunctionPointer(Slot(dc, 47), typeof(CopyResDel));
            FlushDel Flush = (FlushDel)Marshal.GetDelegateForFunctionPointer(Slot(dc, 111), typeof(FlushDel));
            UpdateSubDel UpdateSub = (UpdateSubDel)Marshal.GetDelegateForFunctionPointer(Slot(dc, 48), typeof(UpdateSubDel));
            MapDel Map = (MapDel)Marshal.GetDelegateForFunctionPointer(Slot(dc, 14), typeof(MapDel));
            UnmapDel Unmap = (UnmapDel)Marshal.GetDelegateForFunctionPointer(Slot(dc, 15), typeof(UnmapDel));
            GetRemovedDel GetRemoved = (GetRemovedDel)Marshal.GetDelegateForFunctionPointer(Slot(dev, 39), typeof(GetRemovedDel));
            GetFLDel GetFL = (GetFLDel)Marshal.GetDelegateForFunctionPointer(Slot(dev, 37), typeof(GetFLDel));
            sb.AppendLine("   featureLevel slot37 = " + H(GetFL(dev)));

            const int W = 2048, HH = 2048;
            int bytes = W * HH * 4;

            TEXDESC td = new TEXDESC();
            td.Width = W; td.Height = HH; td.MipLevels = 1; td.ArraySize = 1;
            td.Format = 87; td.SampleCount = 1; td.SampleQuality = 0;
            td.Usage = 0; td.BindFlags = 0x8; td.CPUAccessFlags = 0; td.MiscFlags = 0; td.NodeMask = 0;
            int tsize = Marshal.SizeOf(typeof(TEXDESC));
            IntPtr ptd = Marshal.AllocHGlobal(tsize);
            for (int k = 0; k < tsize; k += 4) Marshal.WriteInt32(ptd, k, 0);
            Marshal.StructureToPtr(td, ptd, false);

            IntPtr texA = IntPtr.Zero, texB = IntPtr.Zero;
            int ha = CreateTex(dev, ptd, IntPtr.Zero, out texA);
            int hb = CreateTex(dev, ptd, IntPtr.Zero, out texB);
            sb.AppendLine("   default tex A hr=" + H(ha) + " B hr=" + H(hb));

            IntPtr src = Marshal.AllocHGlobal(bytes);
            IntPtr dst = Marshal.AllocHGlobal(bytes);
            for (int k = 0; k < bytes; k += 4) Marshal.WriteInt32(src, k, 0x11223344);

            if (ha == 0 && hb == 0)
            {
                UpdateSub(dc, texA, 0, IntPtr.Zero, src, W * 4, 0);
                Flush(dc);

                double[] th = Times(20, delegate () { UpdateSub(dc, texA, 0, IntPtr.Zero, src, W * 4, 0); Flush(dc); });
                sb.AppendLine("   H2D UpdateSubresource 16MB: " + Stat(th) + " -> " + Gb(bytes, th[th.Length / 2]) + " GB/s (median)");

                double[] td2 = Times(20, delegate () { CopyRes(dc, texB, texA); Flush(dc); });
                sb.AppendLine("   D2D CopyResource 16MB:      " + Stat(td2) + " -> " + Gb(bytes, td2[td2.Length / 2]) + " GB/s (median)");

                // staging texture for D2H readback
                TEXDESC sd = new TEXDESC();
                sd.Width = W; sd.Height = HH; sd.MipLevels = 1; sd.ArraySize = 1;
                sd.Format = 87; sd.SampleCount = 1; sd.SampleQuality = 0;
                sd.Usage = 3; sd.BindFlags = 0; sd.CPUAccessFlags = 0x20000; sd.MiscFlags = 0; sd.NodeMask = 0;
                IntPtr psd = Marshal.AllocHGlobal(tsize);
                for (int k = 0; k < tsize; k += 4) Marshal.WriteInt32(psd, k, 0);
                Marshal.StructureToPtr(sd, psd, false);
                IntPtr staging;
                int hs = CreateTex(dev, psd, IntPtr.Zero, out staging);
                Marshal.DestroyStructure(psd, typeof(TEXDESC));
                Marshal.FreeHGlobal(psd);
                sb.AppendLine("   staging tex hr=" + H(hs) + " p=" + staging.ToString("X"));

                if (hs == 0 && staging != IntPtr.Zero)
                {
                    double[] ts = Times(10, delegate ()
                    {
                        CopyRes(dc, staging, texA);
                        Flush(dc);
                        MAPPED mp;
                        int hm = Map(dc, staging, 0, 1, 0, out mp);
                        if (hm == 0 && mp.pData != IntPtr.Zero)
                        {
                            int rp = (int)mp.RowPitch;
                            if (rp <= 0) rp = W * 4;
                            for (int y = 0; y < HH; y++) CopyMemory(Off(dst, (long)y * (W * 4)), Off(mp.pData, (long)y * rp), (UIntPtr)(W * 4));
                        }
                        Unmap(dc, staging, 0);
                    });
                    sb.AppendLine("   D2H copy+Map+memcpy 16MB: " + Stat(ts) + " -> " + Gb(bytes, ts[ts.Length / 2]) + " GB/s (median)");
                    MAPPED mp2;
                    int hm2 = Map(dc, staging, 0, 1, 0, out mp2);
                    sb.AppendLine("   Map(staging) hr=" + H(hm2) + " data=" + mp2.pData.ToString("X") + " rowPitch=" + mp2.RowPitch + " depthPitch=" + mp2.DepthPitch);
                    Unmap(dc, staging, 0);
                }
            }

            sb.AppendLine("   removed=" + H(GetRemoved(dev)));
            Marshal.FreeHGlobal(src);
            Marshal.FreeHGlobal(dst);
            Marshal.DestroyStructure(ptd, typeof(TEXDESC));
            Marshal.FreeHGlobal(ptd);
        }

        double[] tc = Times(20, delegate () { CopyMemory(dst2, src2, (UIntPtr)(2048 * 2048 * 4)); });
        sb.AppendLine("CPU memcpy 16MB: " + Stat(tc) + " -> " + Gb(2048 * 2048 * 4, tc[tc.Length / 2]) + " GB/s (median)");

        sb.Append("done");
        return sb.ToString();
    }

    static IntPtr src2 = Marshal.AllocHGlobal(2048 * 2048 * 4);
    static IntPtr dst2 = Marshal.AllocHGlobal(2048 * 2048 * 4);
}
