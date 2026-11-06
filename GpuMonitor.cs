// ============================================================================
//  GPU / PCIe Monitor  -  moniteur temps reel de la liaison PCIe et de la
//  bande passante reelle des cartes graphiques.
//
//  C# 5 uniquement (csc 4.8 / Add-Type CodeDom).
//  Aucune interface COM declaree (ComImport) : le pilote Adreno de cette
//  machine refuse QueryInterface -> appels par table de fonctions (vtable).
//  DXGI est egalement indisponible ici (E_NOINTERFACE) : non utilise.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

// ============================================================================
//  1. LIAISON PCIe : proprietes PnP (cfgmgr32)
// ============================================================================
[StructLayout(LayoutKind.Sequential)]
public struct DEVPROPKEY
{
    public Guid fmtid;
    public uint pid;
}

public sealed class PcieLink
{
    public string InstanceId = "";
    public string Name = "";
    public uint DevInst;
    public uint CurSpeed, CurWidth, MaxSpeed, MaxWidth;
    public bool HasCurSpeed, HasCurWidth, HasMaxSpeed, HasMaxWidth;

    public bool HasLink { get { return HasCurSpeed && HasCurWidth; } }
    /// <summary>Le peripherique est bien un peripherique PCI Express (au moins une
    /// des quatre proprietes de liaison est exposee par le pilote PCI).</summary>
    public bool HasPci
    {
        get { return HasCurSpeed || HasCurWidth || HasMaxSpeed || HasMaxWidth; }
    }

    public static string GenName(int g)
    {
        if (g == 1) return "Gen1 (2,5 GT/s)";
        if (g == 2) return "Gen2 (5 GT/s)";
        if (g == 3) return "Gen3 (8 GT/s)";
        if (g == 4) return "Gen4 (16 GT/s)";
        if (g == 5) return "Gen5 (32 GT/s)";
        if (g == 6) return "Gen6 (64 GT/s)";
        return "Gen" + g.ToString(CultureInfo.InvariantCulture);
    }

    // debit brut par voie, en Gbit/s (2,5 / 5 / 8 / 16 / 32 / 64 GT/s arrondis)
    public static double GBsPerLane(int gen)
    {
        if (gen <= 0) return 0.0;
        if (gen == 1) return 2.0;      // 2,5 GT/s -> 0,25 Go/s par voie
        if (gen == 2) return 4.0;      // 5        -> 0,5
        if (gen == 3) return 7.877;    // 8        -> 0,9846
        if (gen == 4) return 15.754;   // 16       -> 1,969
        if (gen == 5) return 31.508;   // 32       -> 3,938
        if (gen == 6) return 63.016;   // 64       -> 7,877
        return gen * 3.938;
    }

    // debit theorique en GiB/s : Gbit/s x voies / 8 bits, puis base 1024
    public static double GBs(int gen, int width)
    {
        if (gen <= 0 || width <= 0) return 0.0;
        return GBsPerLane(gen) * width / 8.0 / 1.073741824;
    }

    public double CurrentGBs() { return GBs((int)CurSpeed, (int)CurWidth); }
    public double MaxGBs() { return GBs((int)MaxSpeed, (int)MaxWidth); }

    public string LinkText()
    {
        if (!HasLink) return "n/a";
        return GenName((int)CurSpeed) + " x" + CurWidth.ToString(CultureInfo.InvariantCulture);
    }

    public string WidthText()
    {
        if (!HasCurWidth) return "n/a";
        return "x" + CurWidth.ToString(CultureInfo.InvariantCulture);
    }
}

public static class Pcie
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int ulFlags);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propKey,
                                               out int propType, IntPtr buf, ref int size, int flags);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    static extern int CM_Get_Child(out uint child, uint devInst, int ulFlags);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    static extern int CM_Get_Sibling(out uint sibling, uint devInst, int ulFlags);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    static extern int CM_Get_Parent(out uint parent, uint devInst, int ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int CM_Get_Device_IDW(uint devInst, char[] buf, int len, int ulFlags);

    const int CR_SUCCESS = 0;
    const int CR_NO_SUCH_VALUE = 3;
    const int CR_INSUFFICIENT_BUFFER = 0x103;   // 259 : la taille requise est renvoyee dans size

    static readonly Guid PciSet = new Guid("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62");
    static readonly Guid DeviceSet = new Guid("A45C254E-DF1C-4ECD-8020-67D146A850E0"); // DeviceDesc / FriendlyName
    static readonly Guid NameSet = new Guid("482BE8AE-677D-41C5-BB8E-1E85765AE41A");   // Device_Name

    // Lit une propriete 32 bits. Le tampon est alloue avant l'appel : la
    // variante « tampon nul pour connaitre la taille » ne remplit pas size sur
    // toutes les versions de Windows et faisait perdre toutes les liaisons.
    static bool ReadU32(uint devInst, Guid set, uint pid, out uint value)
    {
        value = 0;
        DEVPROPKEY k = new DEVPROPKEY();
        k.fmtid = set;
        k.pid = pid;
        int size = 8;
        int type;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            int cr = CM_Get_DevNode_PropertyW(devInst, ref k, out type, buf, ref size, 0);
            if (cr == CR_INSUFFICIENT_BUFFER && size >= 4)
            {
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size);
                cr = CM_Get_DevNode_PropertyW(devInst, ref k, out type, buf, ref size, 0);
            }
            if (cr != CR_SUCCESS || size < 4) return false;
            value = (uint)Marshal.ReadInt32(buf);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static string ReadString(uint devInst, Guid set, uint pid)
    {
        DEVPROPKEY k = new DEVPROPKEY();
        k.fmtid = set;
        k.pid = pid;
        int size = 1024;
        int type;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            int cr = CM_Get_DevNode_PropertyW(devInst, ref k, out type, buf, ref size, 0);
            if (cr == CR_INSUFFICIENT_BUFFER && size > 2)
            {
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size);
                cr = CM_Get_DevNode_PropertyW(devInst, ref k, out type, buf, ref size, 0);
            }
            if (cr != CR_SUCCESS || size <= 2) return null;
            int chars = (size - 2) / 2;
            if (chars > 512) chars = 512;
            return Marshal.PtrToStringUni(buf, chars);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static string ReadId(uint devInst)
    {
        char[] b = new char[512];
        if (CM_Get_Device_IDW(devInst, b, b.Length, 0) != CR_SUCCESS) return "";
        return new string(b).TrimEnd('\0');
    }

    // Le code de retour est conserve : sans lui, un echec de localisation etait
    // impossible a distinguer d'une carte sans liaison PCIe.
    public static string Cr(int code)
    {
        return "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
    }

    // cr = -1 quand WMI n'a fourni aucun identifiant ; sinon le code cfgmgr32.
    static uint Locate(string instanceId, out int cr)
    {
        uint di;
        cr = -1;
        if (instanceId == null || instanceId.Length == 0) return 0;
        cr = CM_Locate_DevNodeW(out di, instanceId, 0);
        if (cr != CR_SUCCESS) return 0;
        return di;
    }

    // Plan B : CM_Locate_DevNodeW peut rejeter l'identifiant renvoye par WMI
    // (echappements differents, peripherique masque). On cherche alors une
    // correspondance dans le parcours complet du bus.
    static PcieLink FindByInstanceId(string instanceId, out string chain)
    {
        chain = "";
        List<PcieLink> all = Scan();
        PcieLink prefixe = null;
        foreach (PcieLink l in all)
        {
            if (string.Equals(l.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                return l;
            if (prefixe == null && l.InstanceId != null &&
                l.InstanceId.StartsWith(instanceId, StringComparison.OrdinalIgnoreCase))
                prefixe = l;
        }
        if (prefixe != null)
        {
            chain = "identifiant rapproche : " + prefixe.InstanceId;
            return prefixe;
        }
        return null;
    }

    static PcieLink Make(uint devInst)
    {
        PcieLink l = new PcieLink();
        l.DevInst = devInst;
        l.InstanceId = ReadId(devInst);
        uint v;
        if (ReadU32(devInst, PciSet, 9, out v)) { l.CurSpeed = v; l.HasCurSpeed = true; }
        if (ReadU32(devInst, PciSet, 10, out v)) { l.CurWidth = v; l.HasCurWidth = true; }
        if (ReadU32(devInst, PciSet, 11, out v)) { l.MaxSpeed = v; l.HasMaxSpeed = true; }
        if (ReadU32(devInst, PciSet, 12, out v)) { l.MaxWidth = v; l.HasMaxWidth = true; }
        string s = ReadString(devInst, DeviceSet, 14);            // FriendlyName
        if (s == null || s.Length == 0) s = ReadString(devInst, DeviceSet, 2);   // DeviceDesc
        if (s == null || s.Length == 0) s = ReadString(devInst, NameSet, 12);    // Name
        if (s == null || s.Length == 0) s = WmiName(l.InstanceId);               // WMI
        l.Name = (s == null || s.Length == 0) ? l.InstanceId : s;
        return l;
    }

    // Les proprietes "jolis noms" du noeud (DeviceDesc/FriendlyName/Name) sont
    // absentes sur cette machine (CR_NO_SUCH_VALUE). Seule la base WMI fournit
    // les noms localises des peripheriques PCI : on les charge une fois.
    static Dictionary<string, string> names;

    static string WmiName(string instanceId)
    {
        if (instanceId == null || instanceId.Length == 0) return "";
        if (names == null)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ManagementObjectSearcher se = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI%'");
                foreach (ManagementObject mo in se.Get())
                {
                    string id = null, nm = null;
                    try
                    {
                        object a = mo["PNPDeviceID"];
                        object b = mo["Name"];
                        if (a != null) id = a.ToString();
                        if (b != null) nm = b.ToString();
                    }
                    catch (Exception) { }
                    if (id != null && nm != null && id.Length > 0 && nm.Length > 0 && !d.ContainsKey(id))
                        d[id] = nm;
                }
            }
            catch (Exception) { }
            names = d;
        }
        if (names.ContainsKey(instanceId)) return names[instanceId];
        return "";
    }

    static void Walk(uint devInst, List<PcieLink> outList, int depth)
    {
        if (depth > 40 || outList.Count > 400) return;
        uint child;
        if (CM_Get_Child(out child, devInst, 0) != CR_SUCCESS) return;
        while (true)
        {
            PcieLink l = Make(child);
            if (l.HasLink) outList.Add(l);
            Walk(child, outList, depth + 1);
            uint sib;
            if (CM_Get_Sibling(out sib, child, 0) != CR_SUCCESS) break;
            child = sib;
        }
    }

    // Tous les peripheriques exposant une liaison PCIe (ponts inclus).
    public static List<PcieLink> Scan()
    {
        List<PcieLink> list = new List<PcieLink>();
        uint root;
        if (CM_Locate_DevNodeW(out root, null, 0) != CR_SUCCESS) return list;
        Walk(root, list, 0);
        list.Sort(delegate(PcieLink a, PcieLink b)
        {
            return string.Compare(a.InstanceId, b.InstanceId, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    // Remonte la hierarchie PnP jusqu'a un peripherique exposant des proprietes
    // de liaison PCIe. Une carte graphique est un ENDPOINT PCIe : c'est elle qui
    // publie CurrentLinkSpeed/CurrentLinkWidth, il ne faut donc pas ignorer le
    // premier noeud (erreur corrigee ici : le test i > 0 faisait remonter jusqu'au
    // pont racine, qui n'expose aucune de ces proprietes, d'ou "non reliee en PCIe").
    public static PcieLink FindUpChain(string instanceId, out string chain)
    {
        chain = "";
        int cr;
        uint di = Locate(instanceId, out cr);
        if (di == 0)
        {
            PcieLink alt = FindByInstanceId(instanceId, out chain);
            if (alt == null)
            {
                if (cr == -1)
                    chain = "WMI n'a fourni aucun identifiant PnP pour cette carte.";
                else
                    chain = "CM_Locate_DevNodeW a echoue (code " + Cr(cr) + ") et " +
                            "l'identifiant n'a ete trouve nulle part sur le bus.";
                return null;
            }
            if (alt.HasLink) return alt;
            di = alt.DevInst;
        }
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 12; i++)
        {
            PcieLink l = Make(di);
            if (sb.Length > 0) sb.Append(" -> ");
            sb.Append(l.Name);
            if (l.HasLink) { chain = sb.ToString(); return l; }
            uint parent;
            if (CM_Get_Parent(out parent, di, 0) != CR_SUCCESS) break;
            if (parent == di) break;
            di = parent;
        }
        chain = sb.ToString();
        if (chain.Length == 0)
            chain = "Aucun peripherique accessible au-dessus de cette carte.";
        return null;
    }
}

// ============================================================================
//  2. PERIPHERIQUES GRAPHIQUES (WMI, pas de DXGI)
// ============================================================================
public sealed class GpuInfo
{
    public string Name = "";
    public string DeviceId = "";
    public string DriverVersion = "";
    public string DriverDate = "";
    public string PnpId = "";      // PNPDeviceID (sert a retrouver le noeud PCIe)
    public string Compat = "";     // fabricant du pilote
    public string Resolution = ""; // definition courante
    public long DedicatedMb;       // -1 = non renseigne par WMI
    public bool IsHardware;
    public PcieLink Link;          // null si non relie par PCIe
    public string Chain = "";
    public string Error = "";

    public string VramText()
    {
        if (DedicatedMb < 0) return "non renseignee (memoire partagee)";
        return DedicatedMb.ToString(CultureInfo.InvariantCulture) + " Mo";
    }
}

public static class Gpus
{
    public static List<GpuInfo> Enumerate()
    {
        List<GpuInfo> list = new List<GpuInfo>();

        ManagementObjectCollection col;
        try
        {
            ManagementObjectSearcher se = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            col = se.Get();
        }
        catch (Exception ex)
        {
            GpuInfo g = new GpuInfo();
            g.Name = "Erreur WMI (requete) : " + ex.GetType().Name + " : " + ex.Message;
            list.Add(g);
            return list;
        }

        foreach (ManagementObject mo in col)
        {
            GpuInfo g = new GpuInfo();
            g.DedicatedMb = -1;
            g.Name = Str(mo, "Name");
            g.DeviceId = Str(mo, "DeviceID");
            g.PnpId = Str(mo, "PNPDeviceID");
            g.DriverVersion = Str(mo, "DriverVersion");
            g.DriverDate = Str(mo, "DriverDate");
            if (g.DriverDate.Length > 10) g.DriverDate = g.DriverDate.Substring(0, 10);
            g.Compat = Str(mo, "AdapterCompatibility");
            string chRes = Str(mo, "CurrentHorizontalResolution");
            string cvRes = Str(mo, "CurrentVerticalResolution");
            if (chRes.Length > 0 && cvRes.Length > 0) g.Resolution = chRes + " x " + cvRes;

            // AdapterRAM est absent/nul/0 sur les GPU integres : memoire partagee avec le systeme.
            object ram = mo["AdapterRAM"];
            if (ram != null)
            {
                try
                {
                    long v = Convert.ToInt64(ram, CultureInfo.InvariantCulture);
                    if (v < 0) v += 4294967296L;   // uint32 qui depasse
                    if (v > 0) g.DedicatedMb = v / 1048576L;
                }
                catch (Exception) { }
            }

            // SWD\... = pilote indirect (affichage a distance), pas un materiel.
            g.IsHardware = g.PnpId.Length > 0 &&
                           !g.PnpId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase);

            try
            {
                string chain;
                g.Link = Pcie.FindUpChain(g.PnpId, out chain);
                g.Chain = chain;
            }
            catch (Exception ex)
            {
                g.Chain = "chaine PCIe indisponible : " + ex.GetType().Name + " : " + ex.Message;
            }

            // Explique pourquoi les colonnes PCIe restent vides au lieu de laisser
            // l'utilisateur deviner s'il s'agit d'une panne ou d'un cas normal.
            if (g.Link == null)
            {
                if (g.PnpId.Length == 0)
                    g.Error = "WMI ne fournit pas l'identifiant du peripherique : rapprochement PCIe impossible.";
                else if (g.PnpId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase))
                    g.Error = "Pilote d'affichage logiciel : aucune carte physique derriere ce nom.";
                else if (g.Chain.Length == 0)
                    g.Error = "Peripherique introuvable dans l'arborescence PnP (identifiant " + g.PnpId + ").";
                else if (g.PnpId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                    g.Error = "Carte PCI detectee mais aucune liaison PCIe lisible : " + g.Chain;
                else
                    g.Error = "Aucune liaison PCI Express au-dessus de ce peripherique (GPU du SoC ou " +
                              "plateforme sans bus PCIe visible). Les debits PCIe ne sont pas applicables ; " +
                              "utilisez la mesure des transferts.";
            }

            if (g.Name.Length == 0) g.Name = "(nom illisible) " + g.DeviceId;
            list.Add(g);
        }
        return list;
    }

    // Lit une propriete WMI sans jamais lever : une propriete absente de la classe
    // (Win32_VideoController n'a pas PNPClass ici) ferait sinon tout echouer.
    static string Str(ManagementObject mo, string p)
    {
        try
        {
            object o = mo[p];
            return o == null ? "" : o.ToString();
        }
        catch (Exception) { return ""; }
    }
}

// ============================================================================
//  3. MESURE DE BANDE PASSANTE GPU (Direct3D 11, appels vtable bruts)
// ============================================================================
[StructLayout(LayoutKind.Sequential)]
public struct TEXDESC
{
    public int Width, Height, MipLevels, ArraySize, Format;
    public int SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags, NodeMask;
}

[StructLayout(LayoutKind.Sequential)]
public struct MAPPED
{
    public IntPtr pData;
    public uint RowPitch;
    public uint DepthPitch;
}

public delegate int CreateTexDel(IntPtr self, IntPtr pDesc, IntPtr pInit, out IntPtr ppTex);
public delegate void CopyResDel(IntPtr self, IntPtr dst, IntPtr src);
public delegate void FlushDel(IntPtr self);
public delegate void UpdateSubDel(IntPtr self, IntPtr dst, int dstSub, IntPtr pBox, IntPtr pData, int rowPitch, int depthPitch);
public delegate int MapDel(IntPtr self, IntPtr res, int subres, int mapType, uint flags, out MAPPED pMap);
public delegate void UnmapDel(IntPtr self, IntPtr res, int subres);
public delegate int GetRemovedDel(IntPtr self);
public delegate int GetFLDel(IntPtr self);

public sealed class BenchRow
{
    public string Test = "";
    public bool Ok;
    public double MinMs, MedMs, MaxMs, GBs;
    public string Note = "";
}

public static class GpuBench
{
    [DllImport("d3d11.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr hRt,
                                        uint flags, IntPtr pLevels, uint nLevels,
                                        uint version, out IntPtr ppDev, out int lvl, out IntPtr ppCtx);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    static extern void CopyMemory(IntPtr dst, IntPtr src, UIntPtr len);

    static IntPtr Slot(IntPtr o, int i) { return Marshal.ReadIntPtr(Marshal.ReadIntPtr(o), i * IntPtr.Size); }
    static IntPtr Off(IntPtr p, long o) { return new IntPtr(p.ToInt64() + o); }
    static string HrText(int hr) { return hr == 0 ? "ok" : ("0x" + hr.ToString("X8", CultureInfo.InvariantCulture)); }

    static CreateTexDel CreateTex;
    static CopyResDel CopyRes;
    static FlushDel Flush;
    static UpdateSubDel UpdateSub;
    static MapDel Map;
    static UnmapDel Unmap;
    static GetRemovedDel GetRemoved;

    static IntPtr dev, dc, texA, texB, staging, src, dst;
    static int curW, curH, curBytes, curType = -1, srcCap;
    static string status = "non initialise";

    public static string Status { get { return status; } }
    public static int FeatureLevel;
    public static bool IsReady { get { return dev != IntPtr.Zero && texA != IntPtr.Zero && texB != IntPtr.Zero; } }

    public static string DescribeDriver(int driverType)
    {
        if (driverType == 1) return "Materiel (D3D_DRIVER_TYPE_HARDWARE)";
        if (driverType == 5) return "Logiciel (WARP)";
        return "type " + driverType.ToString(CultureInfo.InvariantCulture);
    }

    // Alloue (ou realoue) l'appareil et les textures pour une taille donnee.
    public static bool Ensure(int driverType, int megaBytes)
    {
        int bytes = megaBytes * 1048576;
        int side = (int)Math.Round(Math.Sqrt(bytes / 4.0));
        int w = side, h = side;
        if (w * h * 4 != bytes) { w = 1024; h = bytes / (4 * w); }

        if (dev != IntPtr.Zero && curType == driverType && curBytes == bytes && IsReady) return true;

        ReleaseAll();

        int lvl = 0;
        int hr = D3D11CreateDevice(IntPtr.Zero, driverType, IntPtr.Zero, 0, IntPtr.Zero, 0, 7,
                                   out dev, out lvl, out dc);
        FeatureLevel = lvl;
        if (hr != 0 || dev == IntPtr.Zero || dc == IntPtr.Zero)
        {
            dev = IntPtr.Zero;
            status = "D3D11CreateDevice echoue hr=0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
            return false;
        }

        CreateTex = (CreateTexDel)Marshal.GetDelegateForFunctionPointer(Slot(dev, 5), typeof(CreateTexDel));
        GetRemoved = (GetRemovedDel)Marshal.GetDelegateForFunctionPointer(Slot(dev, 39), typeof(GetRemovedDel));
        GetFLDel gfl = (GetFLDel)Marshal.GetDelegateForFunctionPointer(Slot(dev, 37), typeof(GetFLDel));
        FeatureLevel = gfl(dev);

        CopyRes = (CopyResDel)Marshal.GetDelegateForFunctionPointer(Slot(dc, 47), typeof(CopyResDel));
        Flush = (FlushDel)Marshal.GetDelegateForFunctionPointer(Slot(dc, 111), typeof(FlushDel));
        UpdateSub = (UpdateSubDel)Marshal.GetDelegateForFunctionPointer(Slot(dc, 48), typeof(UpdateSubDel));
        Map = (MapDel)Marshal.GetDelegateForFunctionPointer(Slot(dc, 14), typeof(MapDel));
        Unmap = (UnmapDel)Marshal.GetDelegateForFunctionPointer(Slot(dc, 15), typeof(UnmapDel));

        curW = w; curH = h; curBytes = bytes; curType = driverType;

        if (srcCap < bytes)
        {
            if (src != IntPtr.Zero) { Marshal.FreeHGlobal(src); Marshal.FreeHGlobal(dst); }
            src = Marshal.AllocHGlobal(bytes);
            dst = Marshal.AllocHGlobal(bytes);
            srcCap = bytes;
            for (int i = 0; i < bytes; i += 4) Marshal.WriteInt32(Off(src, i), 0x11223344);
        }

        TEXDESC t = new TEXDESC();
        t.Width = w; t.Height = h; t.MipLevels = 1; t.ArraySize = 1;
        t.Format = 87;              // DXGI_FORMAT_B8G8R8A8_UNORM
        t.SampleCount = 1; t.SampleQuality = 0;
        t.Usage = 0;                // DEFAULT
        t.BindFlags = 0x8;          // SHADER_RESOURCE
        t.CPUAccessFlags = 0; t.MiscFlags = 0; t.NodeMask = 0;

        IntPtr ptd = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TEXDESC)));
        try
        {
            for (int i = 0; i < Marshal.SizeOf(typeof(TEXDESC)); i += 4) Marshal.WriteInt32(ptd, i, 0);
            Marshal.StructureToPtr(t, ptd, false);

            int hrA = 1, hrB = 1, hrS = 1;
            for (int tryN = 0; tryN < 4 && hrA != 0; tryN++) hrA = CreateTex(dev, ptd, IntPtr.Zero, out texA);
            for (int tryN = 0; tryN < 4 && hrB != 0; tryN++) hrB = CreateTex(dev, ptd, IntPtr.Zero, out texB);

            TEXDESC sd = t;
            sd.Usage = 3;            // STAGING
            sd.BindFlags = 0;        // obligatoire sur ce pilote
            sd.CPUAccessFlags = 0x20000; // READ
            IntPtr psd = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TEXDESC)));
            try
            {
                for (int i = 0; i < Marshal.SizeOf(typeof(TEXDESC)); i += 4) Marshal.WriteInt32(psd, i, 0);
                Marshal.StructureToPtr(sd, psd, false);
                for (int tryN = 0; tryN < 4 && hrS != 0; tryN++) hrS = CreateTex(dev, psd, IntPtr.Zero, out staging);
            }
            finally { Marshal.DestroyStructure(psd, typeof(TEXDESC)); Marshal.FreeHGlobal(psd); }

            status = "appareil D3D11 ok (Feature Level 0x" + FeatureLevel.ToString("X4", CultureInfo.InvariantCulture) +
                     "), texture " + w + "x" + h + " ; creation A=" + HrText(hrA) + ", B=" + HrText(hrB) +
                     ", staging=" + HrText(hrS);
            return hrA == 0 && hrB == 0;
        }
        finally { Marshal.DestroyStructure(ptd, typeof(TEXDESC)); Marshal.FreeHGlobal(ptd); }
    }

    static void ReleaseAll()
    {
        texA = IntPtr.Zero; texB = IntPtr.Zero; staging = IntPtr.Zero;
        dev = IntPtr.Zero; dc = IntPtr.Zero;
        curType = -1; curBytes = 0;
    }

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

    static double Gb(int bytes, double ms)
    {
        if (ms <= 0) return 0;
        return bytes / (ms / 1000.0) / 1073741824.0;
    }

    static BenchRow Row(string test, double[] t, int bytes, string note)
    {
        BenchRow r = new BenchRow();
        r.Test = test;
        if (t == null || t.Length == 0) { r.Ok = false; r.Note = "n/a"; return r; }
        r.Ok = true;
        r.MinMs = t[0];
        r.MedMs = t[t.Length / 2];
        r.MaxMs = t[t.Length - 1];
        r.GBs = Gb(bytes, r.MedMs);
        r.Note = note;
        return r;
    }

    static BenchRow MemcpyRow(int bytes, int reps, string note)
    {
        BenchRow r = new BenchRow();
        r.Test = "Reference CPU (memcpy)";
        if (src == IntPtr.Zero) { r.Ok = false; r.Note = "memoire de reference indisponible"; return r; }
        double[] t = Times(reps, delegate () { CopyMemory(dst, src, (UIntPtr)bytes); });
        return Row("Reference CPU (memcpy)", t, bytes, note);
    }

    public static List<BenchRow> Measure(int reps)
    {
        List<BenchRow> rows = new List<BenchRow>();
        if (!IsReady)
        {
            BenchRow r = new BenchRow();
            r.Test = "appareil D3D11";
            r.Ok = false;
            r.Note = status;
            rows.Add(r);
            return rows;
        }

        int bytes = curBytes;

        // echauffement
        UpdateSub(dc, texA, 0, IntPtr.Zero, src, curW * 4, 0);
        Flush(dc);

        rows.Add(Row("CPU -> GPU (H2D)", Times(reps, delegate ()
        {
            UpdateSub(dc, texA, 0, IntPtr.Zero, src, curW * 4, 0);
            Flush(dc);
        }), bytes, "UpdateSubresource + Flush"));

        BenchRow d2d = Row("GPU -> GPU (D2D)", Times(reps, delegate ()
        {
            CopyRes(dc, texB, texA);
            Flush(dc);
        }), bytes, "CopyResource + Flush");
        rows.Add(d2d);

        if (staging != IntPtr.Zero)
        {
            rows.Add(Row("GPU -> CPU (D2H)", Times(reps, delegate ()
            {
                CopyRes(dc, staging, texA);
                Flush(dc);
                MAPPED mp;
                int hm = Map(dc, staging, 0, 1, 0, out mp);
                if (hm == 0 && mp.pData != IntPtr.Zero)
                {
                    int rp = (int)mp.RowPitch;
                    if (rp <= 0) rp = curW * 4;
                    for (int y = 0; y < curH; y++)
                        CopyMemory(Off(dst, (long)y * (curW * 4)), Off(mp.pData, (long)y * rp), (UIntPtr)(curW * 4));
                }
                Unmap(dc, staging, 0);
            }), bytes, "CopyResource + Map(MAP_READ)"));
        }
        else
        {
            BenchRow r = new BenchRow();
            r.Test = "GPU -> CPU (D2H)";
            r.Ok = false;
            r.Note = "texture STAGING indisponible sur ce pilote";
            rows.Add(r);
        }

        BenchRow cpu = MemcpyRow(bytes, reps, "RtlMoveMemory " + (bytes / 1048576).ToString(CultureInfo.InvariantCulture) + " Mo");
        double refGb = cpu.GBs;
        rows.Add(cpu);

        if (d2d.Ok && refGb > 0 && d2d.GBs > refGb * 2.5)
            d2d.Note = "copie elidee par le pilote : non mesurable";

        int removed = GetRemoved(dev);
        if (removed != 0)
        {
            BenchRow r = new BenchRow();
            r.Test = "appareil D3D11";
            r.Ok = false;
            r.Note = "device removed 0x" + removed.ToString("X8", CultureInfo.InvariantCulture);
            rows.Add(r);
        }
        return rows;
    }
}

// ============================================================================
//  4. HISTORIQUE (en memoire uniquement)
// ============================================================================
public sealed class HistEntry
{
    public DateTime T;
    public string Kind = "";   // LIEN / MESURE
    public string Device = "";
    public string Detail = "";
    public string Value = "";
}

public static class History
{
    static List<HistEntry> items = new List<HistEntry>();
    static Dictionary<string, string> lastState = new Dictionary<string, string>();
    static readonly object gate = new object();

    public static int Count { get { lock (gate) return items.Count; } }

    public static List<HistEntry> Snapshot()
    {
        lock (gate) return new List<HistEntry>(items);
    }

    public static void Clear()
    {
        lock (gate) { items.Clear(); lastState.Clear(); }
    }

    // Ajoute une entree seulement si l'etat a change.
    public static bool RecordLink(string key, string name, PcieLink l)
    {
        if (l == null || !l.HasLink) return false;
        string state = l.CurSpeed.ToString(CultureInfo.InvariantCulture) + "x" +
                       l.CurWidth.ToString(CultureInfo.InvariantCulture);
        lock (gate)
        {
            string prev;
            if (lastState.TryGetValue(key, out prev) && prev == state) return false;
            lastState[key] = state;
            HistEntry e = new HistEntry();
            e.T = DateTime.Now;
            e.Kind = "LIEN";
            e.Device = name;
            e.Detail = PcieLink.GenName((int)l.CurSpeed) + " x" + l.CurWidth.ToString(CultureInfo.InvariantCulture) +
                       " (max " + PcieLink.GenName((int)l.MaxSpeed) + " x" + l.MaxWidth.ToString(CultureInfo.InvariantCulture) + ")";
            e.Value = l.CurrentGBs().ToString("0.00", CultureInfo.InvariantCulture) + " Go/s";
            items.Add(e);
            if (items.Count > 5000) items.RemoveAt(0);
            return true;
        }
    }

    // Une entree par mesure de debit (le resultat est conserve, pas seulement l'evenement).
    public static void RecordMeasure(string name, string detail, string value)
    {
        lock (gate)
        {
            HistEntry e = new HistEntry();
            e.T = DateTime.Now;
            e.Kind = "MESURE";
            e.Device = name;
            e.Detail = detail;
            e.Value = value;
            items.Add(e);
            if (items.Count > 5000) items.RemoveAt(0);
        }
    }
}

// ============================================================================
//  5. TRAFIC PCIe REEL MESURE PAR LE PILOTE NVIDIA (nvidia-smi dmon -s t)
//
//  Le banc D3D11 mesure ce que le programme envoie ; lui ne mesure pas ce que
//  la carte recoit vraiment. Le pilote NVIDIA, lui, connait le nombre d'octets
//  qui ont effectivement traverse le lien PCIe. La commande
//      nvidia-smi dmon -s t
//  affiche chaque seconde, par carte, rx pci et tx pci. C'est la valeur reelle.
// ============================================================================
public sealed class NvSample
{
    public DateTime T;
    public int Index;
    public double Rx, Tx;      // Go/s
}

public sealed class NvGpu
{
    public int Index = -1;
    public string Name = "";
    public string Bus = "";
    public double LastRx, LastTx, MaxRx, MaxTx, SumRx, SumTx;
    public long Count;
    public bool Seen;
}

public sealed class NvTraffic
{
    readonly object gate = new object();
    readonly List<NvGpu> gpus = new List<NvGpu>();
    readonly List<NvSample> samples = new List<NvSample>();

    Process proc;
    System.Threading.Thread reader;
    string smiPath = "";
    string status = "non demarre";
    bool running;
    // Colonnes reperees dans l'en-tete (index -1 = pas trouve).
    int rxCol = -1, txCol = -1, idxCol = -1;
    int headerLen;                 // nombre de colonnes annonce par l'en-tete
    bool unitIsGb;                 // l'en-tete annonce « GB/s » au lieu de « MB/s »
    long lastLineTicks;
    string layout = "";            // en-tete reconnu, affiche dans l'interface

    public string Status { get { lock (gate) return status; } }
    public bool IsRunning { get { lock (gate) return running; } }
    public long LastLineMs
    {
        get
        {
            lock (gate)
            {
                if (lastLineTicks == 0) return -1;
                return (DateTime.Now.Ticks - lastLineTicks) / TimeSpan.TicksPerMillisecond;
            }
        }
    }

    // En-tete reconnu par le parseur. Vide tant qu'aucun en-tete n'a ete lu :
    // c'est le moyen le plus sur de verifier que les colonnes sont les bonnes.
    public string Layout
    {
        get { lock (gate) return layout; }
    }

    public List<NvGpu> Gpus()
    {
        lock (gate) return new List<NvGpu>(gpus);
    }

    public List<NvSample> Samples(int max)
    {
        lock (gate)
        {
            int n = samples.Count;
            if (n <= max) return new List<NvSample>(samples);
            return new List<NvSample>(samples.GetRange(n - max, max));
        }
    }

    public int SampleCount() { lock (gate) return samples.Count; }

    public void Clear()
    {
        lock (gate)
        {
            samples.Clear();
            foreach (NvGpu g in gpus) { g.MaxRx = 0; g.MaxTx = 0; g.SumRx = 0; g.SumTx = 0; g.Count = 0; }
        }
    }

    // -------------------------------------------------------------------
    //  Localisation de nvidia-smi
    // -------------------------------------------------------------------
    public static string FindSmi()
    {
        string[] fixed_ = new string[3];
        fixed_[0] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        fixed_[1] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                 @"NVIDIA Corporation\NVSMI\nvidia-smi.exe");
        fixed_[2] = "nvidia-smi.exe";
        foreach (string p in fixed_)
        {
            try { if (File.Exists(p)) return p; }
            catch (Exception) { }
        }
        // PATH
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string d in path.Split(';'))
        {
            if (d.Trim().Length == 0) continue;
            try
            {
                string p = Path.Combine(d.Trim(), "nvidia-smi.exe");
                if (File.Exists(p)) return p;
            }
            catch (Exception) { }
        }
        return null;
    }

    // -------------------------------------------------------------------
    //  Demarrage / arret
    // -------------------------------------------------------------------
    public void Start()
    {
        string smi = FindSmi();
        if (smi == null)
        {
            lock (gate) status = "nvidia-smi introuvable : aucun pilote NVIDIA detecte sur ce PC";
            return;
        }
        smiPath = smi;
        QueryGpus();
        lock (gate)
        {
            if (running) return;
            running = true;
        }
        try
        {
            ProcessStartInfo si = new ProcessStartInfo();
            si.FileName = smi;
            si.Arguments = "dmon -s t";
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            si.RedirectStandardOutput = true;
            si.RedirectStandardError = true;
            si.StandardOutputEncoding = Encoding.UTF8;
            proc = Process.Start(si);
            reader = new System.Threading.Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Start();

            System.Threading.Thread err = new System.Threading.Thread(ReadErr);
            err.IsBackground = true;
            err.Start();
            lock (gate) status = "capture en cours : " + smi + " dmon -s t";
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                running = false;
                status = "demarrage impossible : " + ex.Message;
            }
        }
    }

    public void Stop()
    {
        lock (gate) running = false;
        try { if (proc != null && !proc.HasExited) proc.Kill(); }
        catch (Exception) { }
    }

    void ReadErr()
    {
        try
        {
            if (proc == null) return;
            string err = proc.StandardError.ReadToEnd();
            if (err != null && err.Trim().Length > 0)
                lock (gate) status = "nvidia-smi : " + err.Trim().Replace("\r\n", " ");
        }
        catch (Exception) { }
    }

    void ReadLoop()
    {
        try
        {
            StreamReader sr = proc.StandardOutput;
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                HandleLine(line);
                if (!running) break;
            }
        }
        catch (Exception) { }
        lock (gate) running = false;
    }

    // -------------------------------------------------------------------
    //  Liste des cartes (index, nom, bus) : sert a nommer les courbes
    // -------------------------------------------------------------------
    void QueryGpus()
    {
        try
        {
            ProcessStartInfo si = new ProcessStartInfo();
            si.FileName = smiPath;
            si.Arguments = "--query-gpu=index,name,pci.bus_id --format=csv,noheader";
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            si.RedirectStandardOutput = true;
            si.StandardOutputEncoding = Encoding.UTF8;
            Process p = Process.Start(si);
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            foreach (string raw in o.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(',');
                if (parts.Length < 2) continue;
                NvGpu g = new NvGpu();
                g.Index = ParseInt(parts[0]);
                g.Name = parts[1].Trim();
                if (parts.Length > 2) g.Bus = parts[2].Trim();
                lock (gate) gpus.Add(g);
            }
        }
        catch (Exception) { }
    }

    // -------------------------------------------------------------------
    //  Analyse d'une ligne de dmon
    //
    //  Le nombre et l'ordre des colonnes dependent du pilote et des options.
    //  Formes rencontrees dans les journaux reels :
    //      # gpu rxpci txpci              (dmon -s t)
    //      # Idx MB/s MB/s               (ligne des unites)
    //      # gpu  pwr gtemp mtemp sm mem enc dec rxpci txpci   (dmon -s put)
    //      #Date, Time, gpu, fb, bar1, ... , rxpci, txpci, ...  (GPM, CSV)
    //  Regles appliquees :
    //   - les colonnes rx/tx sont reperees par leur NOM, et seulement sur la
    //     ligne qui contient a la fois un « rx...pci » et un « tx...pci ».
    //     Les colonnes NVLink (nvlrx/nvltx) et GPM (pcirx/pcitx) sont exclues.
    //   - la ligne des unites ne deplace jamais les colonnes deja trouvees ;
    //     elle ne fait que renseigner l'unite.
    //   - si les colonnes sont inconnues, la ligne est IGNOREE. On ne devine
    //     jamais deux nombres au hasard : la consommation (pwr) ou la memoire
    //     utilisee (fb, bar1) sont des nombres tout aussi valides, et cela a
    //     deja produit des pics de 19 Go/s sur une liaison Gen4 x8.
    //   - le pilote affiche « MB/s » mais le compteur NVML travaille en KiB/s :
    //     on divise donc par 1024 pour obtenir des Go/s.
    // -------------------------------------------------------------------
    void HandleLine(string raw)
    {
        if (raw == null) return;
        string line = raw.Trim();
        if (line.Length == 0) return;

        // Le separateur peut etre l'espace, la tabulation ou la virgule (sortie
        // de type CSV). Certaines versions prefixent les lignes de DONNEES par
        // « # » (avec -c N) et les pilotes anciens ecrivent « ## ».
        string body = line;
        if (body[0] == '#') body = body.TrimStart('#').Trim();
        if (body.Length == 0) return;
        string[] f = body.Split(new char[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length == 0) return;

        // Une ligne qui commence par un nombre est une donnee, sinon un en-tete.
        if (!(ParseInt(f[0]) < 0))
        {
            HandleData(f);
            return;
        }

        int hrx = -1, htx = -1, hidx = -1;
        bool hasUnit = false;
        for (int i = 0; i < f.Length; i++)
        {
            string c = f[i].ToLower(CultureInfo.InvariantCulture);
            if (c.IndexOf("/s", StringComparison.Ordinal) >= 0) hasUnit = true;
            if (hrx < 0 && ColIsRx(c)) hrx = i;
            if (htx < 0 && ColIsTx(c)) htx = i;
            if (hidx < 0 && (c == "gpu" || c == "idx" || c == "index")) hidx = i;
        }

        // Vraie ligne de noms : des colonnes rx et tx, et aucune unite.
        if (hrx >= 0 && htx >= 0 && !hasUnit)
        {
            lock (gate)
            {
                rxCol = hrx; txCol = htx; idxCol = hidx; headerLen = f.Length;
                layout = string.Join(" ", f);
            }
            return;
        }

        // Ligne d'unites (ou phrase de description) : elle ne doit surtout pas
        // decaler les colonnes. On n'en lit que l'unite annoncee.
        for (int i = 0; i < f.Length; i++)
        {
            string c = f[i].ToLower(CultureInfo.InvariantCulture);
            if (c.IndexOf("/s", StringComparison.Ordinal) < 0) continue;
            bool gbu = c.IndexOf("gb", StringComparison.Ordinal) >= 0 ||
                       c.IndexOf("gib", StringComparison.Ordinal) >= 0;
            lock (gate) { unitIsGb = gbu; }
            break;
        }
    }

    // Ligne de mesure : les colonnes viennent obligatoirement de l'en-tete.
    void HandleData(string[] f)
    {
        int rxIdx, txIdx, idxIdx;
        bool gb;
        lock (gate) { rxIdx = rxCol; txIdx = txCol; idxIdx = idxCol; gb = unitIsGb; }

        // En-tete pas encore reconnu : on ne devine RIEN. Deviner la premiere
        // colonne numerique suivante a deja affiche 19 Go/s sur du Gen4 x8 :
        // c'etait la memoire video utilisee, exprimee en Mo.
        if (rxIdx < 0 || txIdx < 0) return;
        if (rxIdx >= f.Length || txIdx >= f.Length) return;

        int idx;
        if (idxIdx >= 0 && idxIdx < f.Length) idx = ParseInt(f[idxIdx]);
        else idx = ParseInt(f[0]);
        // L'index d'une carte est petit. Cette borne rejette les variantes ou la
        // premiere colonne est une date (sortie CSV avec --options=DT), qui
        // creaient une fausse carte « GPU 20250819 ».
        if (idx < 0 || idx > 1023) return;

        double rxv = ParseDouble(f[rxIdx]);
        double txv = ParseDouble(f[txIdx]);
        // Le pilote affiche « - » quand le compteur n'est pas disponible.
        if (double.IsNaN(rxv) && double.IsNaN(txv)) return;
        if (double.IsNaN(rxv)) rxv = 0;
        if (double.IsNaN(txv)) txv = 0;

        // Unite : « GB/s » dans l'en-tete, sinon « MB/s » lu en MiB/s.
        double k = 1.0 / 1024.0;
        if (gb) k = 1.0;

        NvSample s = new NvSample();
        s.T = DateTime.Now;
        s.Index = idx;
        s.Rx = rxv * k;
        s.Tx = txv * k;

        lock (gate)
        {
            lastLineTicks = DateTime.Now.Ticks;
            samples.Add(s);
            if (samples.Count > 7200) samples.RemoveAt(0);
            NvGpu g = FindGpu(idx);
            g.LastRx = s.Rx; g.LastTx = s.Tx;
            if (s.Rx > g.MaxRx) g.MaxRx = s.Rx;
            if (s.Tx > g.MaxTx) g.MaxTx = s.Tx;
            g.SumRx += s.Rx; g.SumTx += s.Tx; g.Count++;
            g.Seen = true;
        }
    }

    // Appelle sous verrou.
    NvGpu FindGpu(int idx)
    {
        foreach (NvGpu g in gpus) if (g.Index == idx) return g;
        NvGpu n = new NvGpu();
        n.Index = idx;
        n.Name = "GPU " + idx.ToString(CultureInfo.InvariantCulture);
        gpus.Add(n);
        return n;
    }

    static int ParseInt(string s)
    {
        int v;
        if (int.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
        return -1;
    }

    // Retourne NaN quand ce n'est pas un nombre (le pilote affiche « - »).
    static double ParseDouble(string s)
    {
        double v;
        if (double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
        return double.NaN;
    }

    // Noms de colonnes : « rxpci », « rx.pci », « pcirx ». On exclut les compteurs
    // NVLink (« nvlrx » / « nvltx ») qui ne passent pas par le port PCIe.
    static bool ColIsRx(string c)
    {
        if (c.IndexOf("nvl", StringComparison.Ordinal) >= 0) return false;
        if (c.IndexOf("tx", StringComparison.Ordinal) >= 0) return false;
        return c.IndexOf("rx", StringComparison.Ordinal) >= 0 &&
               c.IndexOf("pci", StringComparison.Ordinal) >= 0;
    }

    static bool ColIsTx(string c)
    {
        if (c.IndexOf("nvl", StringComparison.Ordinal) >= 0) return false;
        if (c.IndexOf("rx", StringComparison.Ordinal) >= 0) return false;
        return c.IndexOf("tx", StringComparison.Ordinal) >= 0 &&
               c.IndexOf("pci", StringComparison.Ordinal) >= 0;
    }

    // -------------------------------------------------------------------
    //  Test du parseur (aucune carte NVIDIA n'est necessaire)
    // -------------------------------------------------------------------
    // Une mise en situation : de vraies sorties de nvidia-smi, avec le rx et le tx attendus.
    private class Fixture
    {
        public string Name;
        public string[] Lines;
        public double[][] Want;   // { index GPU, rx attendu en Go/s, tx attendu en Go/s }

        public Fixture(string name, string[] lines, double[][] want)
        {
            Name = name; Lines = lines; Want = want;
        }
    }

    static Fixture[] ParserFixtures()
    {
        return new Fixture[] {
            new Fixture("A : dmon -s t (format par defaut de l appli)",
                new string[] {
                    "# gpu    rxpci    txpci",
                    "# Idx      MB/s     MB/s",
                    "0      12345     7500",
                    "1       3000      900",
                    "2          0        0",
                    "0      12000     7400",
                    "1          -        -" },
                new double[][] {
                    new double[] { 0, 11.71875, 7.2265625 },
                    new double[] { 1, 2.9296875, 0.87890625 },
                    new double[] { 2, 0, 0 } }),

            new Fixture("B : lignes de donnees prefixees par # (option -c N)",
                new string[] {
                    "# gpu    rxpci    txpci",
                    "# Idx      MB/s     MB/s",
                    "#   0      759     3606",
                    "#   1        0        3" },
                new double[][] {
                    new double[] { 0, 0.7412109375, 3.521484375 },
                    new double[] { 1, 0, 0.0029296875 } }),

            new Fixture("C : en-tetes herites ##",
                new string[] {
                    "## gpu  rxpci  txpci",
                    "## Idx  MB/s   MB/s",
                    "   0     1500    900" },
                new double[][] {
                    new double[] { 0, 1.46484375, 0.87890625 } }),

            new Fixture("D : -s put (pwr/gtemp avant rxpci) = le bug des 19 Go/s",
                new string[] {
                    "# gpu   pwr  gtemp  mtemp   sm   mem   enc   dec  rxpci  txpci",
                    "# Idx   W     C      C      %    %     %     %    MB/s   MB/s",
                    "0       224    43      -     0     0     0     0   19680  16210",
                    "1        30    50      -     0     0     0     0       0      0" },
                new double[][] {
                    new double[] { 0, 19.21875, 15.830078125 },
                    new double[] { 1, 0, 0 } }),

            new Fixture("E : GPM CSV avec --options=DT",
                new string[] {
                    "#Date, Time, gpu, fb, bar1, ccpm, rxpci, txpci, sbecc, dbecc, pci, pcitx, pcirx, nvlrx, nvltx",
                    "#20250819, 17:21:36, Idx, MB, MB, MB, MB/s, MB/s, errs, errs, errs, GPM:MiB/s, GPM:MiB/s, GPM:MiB/s, GPM:MiB/s",
                    "20250819, 17:21:36, 2, 16811, 109, 0, 41115, 2741, 0, 0, 0, 4173, 22148, 0, 0" },
                new double[][] {
                    new double[] { 2, 40.1513671875, 2.6767578125 } }),

            new Fixture("F : colonnes NVLink (ne doivent jamais etre prises)",
                new string[] {
                    "# gpu   rxpci  txpci  nvlrx  nvltx",
                    "# Idx   MB/s   MB/s   MB/s   MB/s",
                    "0         0      3   6316   6833" },
                new double[][] {
                    new double[] { 0, 0, 0.0029296875 } }),

            new Fixture("G : tirets (valeur indisponible)",
                new string[] {
                    "# gpu  rxpci  txpci",
                    "# Idx  MB/s   MB/s",
                    "0        -     500" },
                new double[][] {
                    new double[] { 0, 0, 0.48828125 } })
        };
    }

    public static string SelfTestParser()
    {
        StringBuilder o = new StringBuilder();
        Fixture[] cases = ParserFixtures();
        int fail = 0;
        foreach (Fixture c in cases)
        {
            NvTraffic t = new NvTraffic();
            foreach (string l in c.Lines) t.HandleLine(l);
            List<NvGpu> gs = t.Gpus();
            o.AppendLine("  " + c.Name + " : " + gs.Count.ToString(CultureInfo.InvariantCulture) +
                         " GPU, " + t.SampleCount().ToString(CultureInfo.InvariantCulture) + " echantillons");
            if (gs.Count != c.Want.Length)
            {
                o.AppendLine("     ECHEC : " + c.Want.Length.ToString(CultureInfo.InvariantCulture) +
                             " GPU attendus");
                fail++;
            }
            foreach (double[] w in c.Want)
            {
                int idx = (int) w[0];
                NvGpu found = null;
                foreach (NvGpu g in gs) if (g.Index == idx) found = g;
                if (found == null)
                {
                    o.AppendLine("     ECHEC : GPU " + idx.ToString(CultureInfo.InvariantCulture) + " absent");
                    fail++;
                    continue;
                }
                bool ok = Math.Abs(found.LastRx - w[1]) < 1e-6 && Math.Abs(found.LastTx - w[2]) < 1e-6;
                if (!ok) fail++;
                o.AppendLine("     GPU " + idx.ToString(CultureInfo.InvariantCulture) +
                             " rx " + found.LastRx.ToString("0.000", CultureInfo.InvariantCulture) +
                             " Go/s, tx " + found.LastTx.ToString("0.000", CultureInfo.InvariantCulture) +
                             " Go/s " + (ok ? "OK" :
                             "ECHEC (attendu rx " + w[1].ToString("0.000", CultureInfo.InvariantCulture) +
                             ", tx " + w[2].ToString("0.000", CultureInfo.InvariantCulture) + ")"));
            }
        }
        o.AppendLine("  parseur : " + (fail == 0 ? "OK (" + cases.Length.ToString(CultureInfo.InvariantCulture) +
                     " mises en situation verifiees)" : "ECHEC (" + fail.ToString(CultureInfo.InvariantCulture) + " erreurs)"));
        return o.ToString();
    }
}

// ============================================================================
//  6. AUTO-TEST (verifie le moteur sans interface, via Add-Type)
// ============================================================================
public static class SelfTest
{
    public static string Run()
    {
        StringBuilder o = new StringBuilder();
        o.AppendLine("=== PCIe Scan ===");
        List<PcieLink> ls = Pcie.Scan();
        o.AppendLine("count=" + ls.Count);
        foreach (PcieLink l in ls)
        {
            History.RecordLink(l.InstanceId, l.Name, l);
            o.AppendLine("  " + l.Name + " | " + l.LinkText() + " | " +
                         l.CurrentGBs().ToString("0.00", CultureInfo.InvariantCulture) + " Go/s | max " +
                         l.MaxGBs().ToString("0.00", CultureInfo.InvariantCulture) + " Go/s | " + l.InstanceId);
        }

        o.AppendLine("=== GPU ===");
        foreach (GpuInfo g in Gpus.Enumerate())
        {
            o.AppendLine("  " + g.Name + " [" + g.DeviceId + "] drv=" + g.DriverVersion +
                         " VRAM=" + g.VramText() + " materielle=" + g.IsHardware +
                         " pilote=" + g.Compat + " ecran=" + g.Resolution);
            if (g.Error.Length > 0) o.AppendLine("     erreur: " + g.Error);
            o.AppendLine("     chaine: " + g.Chain);
            o.AppendLine("     lien: " + (g.Link == null ? "AUCUN (non relie par PCIe)" : g.Link.LinkText()));
        }

        o.AppendLine("=== Bench HARDWARE 16 Mo ===");
        if (GpuBench.Ensure(1, 16))
        {
            o.AppendLine("  " + GpuBench.Status);
            foreach (BenchRow r in GpuBench.Measure(10))
            {
                History.RecordMeasure("GPU (materiel)", r.Test + " / 16 Mo",
                    r.Ok ? r.GBs.ToString("0.00", CultureInfo.InvariantCulture) + " Go/s" : "echec");
                o.AppendLine("  " + r.Test + " : " +
                    (r.Ok ? r.MedMs.ToString("0.000", CultureInfo.InvariantCulture) + " ms -> " +
                            r.GBs.ToString("0.00", CultureInfo.InvariantCulture) + " Go/s" : "ECHEC") +
                    " [" + r.Note + "]");
            }
        }
        else o.AppendLine("  " + GpuBench.Status);

        o.AppendLine("=== Trafic PCIe NVIDIA ===");
        string smi = NvTraffic.FindSmi();
        o.AppendLine("  nvidia-smi : " + (smi == null ? "introuvable (pas de pilote NVIDIA sur ce PC)" : smi));
        o.Append(NvTraffic.SelfTestParser());

        o.AppendLine("=== Historique ===");
        foreach (HistEntry e in History.Snapshot())
            o.AppendLine("  " + e.T.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + e.Kind + " " + e.Device + " " + e.Detail + " " + e.Value);
        o.AppendLine("done");
        string txt = o.ToString();
        // Le meme rapport est ecrit dans un fichier : plus simple pour decrire un probleme
        // sur une machine distante (le texte tient dans un fichier, pas dans une capture d'ecran).
        // Trois dossiers sont tries, du plus pratique au plus permissif.
        string nom = "SelfTest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";
        string[] dossiers = new string[3];
        try { dossiers[0] = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName); }
        catch (Exception) { dossiers[0] = null; }
        try { dossiers[1] = Directory.GetCurrentDirectory(); } catch (Exception) { dossiers[1] = null; }
        try { dossiers[2] = Path.GetTempPath(); } catch (Exception) { dossiers[2] = null; }
        for (int i = 0; i < dossiers.Length; i++)
        {
            if (dossiers[i] == null || dossiers[i].Length == 0) continue;
            try
            {
                string p = Path.Combine(dossiers[i], nom);
                File.WriteAllText(p, txt, new UTF8Encoding(false));
                txt += "Rapport sauvegarde : " + p + Environment.NewLine;
                break;
            }
            catch (Exception ex) { txt += "Pas de rapport dans " + dossiers[i] + " : " + ex.Message + Environment.NewLine; }
        }
        return txt;
    }
}

// ============================================================================
//  6. INTERFACE GRAPHIQUE
// ============================================================================
public sealed class MainForm : Form
{
    TabControl tabs = new TabControl();
    ListView lvGpu = new ListView();
    ListView lvBench = new ListView();
    ListView lvPcie = new ListView();
    ListView lvHist = new ListView();
    TextBox txtNote = new TextBox();
    ComboBox cmbSize = new ComboBox();
    ComboBox cmbReps = new ComboBox();
    CheckBox chkAuto = new CheckBox();
    ComboBox cmbInterval = new ComboBox();
    Button btnBench = new Button();
    Button btnClear = new Button();
    Label lblBenchInfo = new Label();
    Label lblTheory = new Label();
    Timer timer = new Timer();
    Chart chart = new Chart();
    ListView lvNv = new ListView();
    Label lblNvStatus = new Label();
    Button btnNv = new Button();
    ComboBox cmbNvWin = new ComboBox();
    NvTraffic nv = new NvTraffic();
    Dictionary<int, double> nvPeak = new Dictionary<int, double>();

    static readonly Color[] NvColors = new Color[] {
        Color.Red, Color.DodgerBlue, Color.Goldenrod, Color.SeaGreen,
        Color.Magenta, Color.DarkCyan, Color.OrangeRed, Color.Gray };

    List<GpuInfo> gpus = new List<GpuInfo>();
    List<PcieLink> links = new List<PcieLink>();

    public MainForm()
    {
        Text = "Moniteur GPU / PCIe - vitesse de transfert et historique des liaisons (v1.4)";
        Width = 1120;
        Height = 720;
        // empeche la fenetre d'etre reduite au point d'ecraser le graphe (hauteur <= 0)
        MinimumSize = new Size(820, 460);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = SystemIcons.Application; } catch { }

        tabs.Dock = DockStyle.Fill;
        TabPage pGpu = new TabPage("Cartes graphiques");
        TabPage pPcie = new TabPage("Liaisons PCIe");
        TabPage pNv = new TabPage("Debit PCIe en direct");
        TabPage pHist = new TabPage("Historique");
        tabs.TabPages.Add(pGpu);
        tabs.TabPages.Add(pPcie);
        tabs.TabPages.Add(pNv);
        tabs.TabPages.Add(pHist);
        Controls.Add(tabs);

        // ---- onglet GPU -------------------------------------------------
        lvGpu.Dock = DockStyle.Top;
        lvGpu.Height = 210;
        lvGpu.View = View.Details;
        lvGpu.FullRowSelect = true;
        lvGpu.MultiSelect = false;
        lvGpu.HideSelection = false;
        AddCols(lvGpu, new string[][] {
            new string[] { "Carte graphique", "290" },
            new string[] { "Liaison PCIe", "150" },
            new string[] { "Debit actuel (Go/s)", "120" },
            new string[] { "Debit max (Go/s)", "110" },
            new string[] { "VRAM dediee", "150" },
            new string[] { "Pilote", "110" },
            new string[] { "Editeur du pilote", "170" },
            new string[] { "Definition ecran", "110" },
            new string[] { "Diagnostic", "300" },
            new string[] { "Identifiant du peripherique", "330" } });

        Panel bar = new Panel();
        bar.Dock = DockStyle.Top;
        bar.Height = 34;
        Label l1 = new Label();
        l1.Text = "Taille du tampon :";
        l1.SetBounds(6, 9, 95, 18);
        cmbSize.SetBounds(103, 6, 80, 24);
        cmbSize.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbSize.Items.AddRange(new object[] { "4 Mo", "16 Mo", "64 Mo" });
        cmbSize.SelectedIndex = 1;
        Label l2 = new Label();
        l2.Text = "Repetitions :";
        l2.SetBounds(196, 9, 70, 18);
        cmbReps.SetBounds(268, 6, 66, 24);
        cmbReps.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbReps.Items.AddRange(new object[] { "5", "10", "20", "50" });
        cmbReps.SelectedIndex = 1;
        btnBench.Text = "Mesurer les transferts";
        btnBench.SetBounds(348, 4, 160, 26);
        btnBench.Click += OnBench;
        lblBenchInfo.Text = "";
        lblBenchInfo.SetBounds(518, 9, 560, 18);
        bar.Controls.Add(l1);
        bar.Controls.Add(cmbSize);
        bar.Controls.Add(l2);
        bar.Controls.Add(cmbReps);
        bar.Controls.Add(btnBench);
        bar.Controls.Add(lblBenchInfo);

        lvBench.Dock = DockStyle.Top;
        lvBench.Height = 170;
        lvBench.View = View.Details;
        lvBench.FullRowSelect = true;
        AddCols(lvBench, new string[][] {
            new string[] { "Test", "210" },
            new string[] { "Min (ms)", "90" },
            new string[] { "Median (ms)", "100" },
            new string[] { "Max (ms)", "90" },
            new string[] { "Debit (Go/s)", "110" },
            new string[] { "Observation", "420" } });

        txtNote.Dock = DockStyle.Fill;
        txtNote.Multiline = true;
        txtNote.ReadOnly = true;
        txtNote.ScrollBars = ScrollBars.Vertical;
        txtNote.BackColor = SystemColors.Control;
        txtNote.Font = new Font("Segoe UI", 9f);
        UpdateNote();

        pGpu.Controls.Add(txtNote);
        pGpu.Controls.Add(lvBench);
        pGpu.Controls.Add(bar);
        pGpu.Controls.Add(lvGpu);

        // ---- onglet PCIe -------------------------------------------------
        Panel bar2 = new Panel();
        bar2.Dock = DockStyle.Top;
        bar2.Height = 34;
        chkAuto.Text = "Rafraichissement automatique";
        chkAuto.SetBounds(8, 8, 210, 20);
        chkAuto.Checked = true;
        cmbInterval.SetBounds(226, 6, 90, 24);
        cmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbInterval.Items.AddRange(new object[] { "1 s", "2 s", "5 s", "10 s" });
        cmbInterval.SelectedIndex = 0;
        cmbInterval.SelectedIndexChanged += delegate { timer.Interval = int.Parse(cmbInterval.SelectedItem.ToString().Split(' ')[0], CultureInfo.InvariantCulture) * 1000; };
        Button btnRescan = new Button();
        btnRescan.Text = "Rescanner";
        btnRescan.SetBounds(326, 4, 100, 26);
        btnRescan.Click += delegate { RefreshPcie(true); };
        bar2.Controls.Add(chkAuto);
        bar2.Controls.Add(cmbInterval);
        bar2.Controls.Add(btnRescan);

        lvPcie.Dock = DockStyle.Fill;
        lvPcie.View = View.Details;
        lvPcie.FullRowSelect = true;
        lvPcie.HideSelection = false;
        lvPcie.SelectedIndexChanged += OnPcieSelect;
        AddCols(lvPcie, new string[][] {
            new string[] { "Peripherique", "330" },
            new string[] { "Liaison actuelle", "140" },
            new string[] { "Largeur", "60" },
            new string[] { "Debit actuel (Go/s)", "120" },
            new string[] { "Liaison max", "140" },
            new string[] { "Debit max (Go/s)", "120" },
            new string[] { "Identifiant", "300" } });

        lblTheory.Dock = DockStyle.Bottom;
        lblTheory.Height = 46;
        lblTheory.TextAlign = ContentAlignment.MiddleLeft;
        lblTheory.Text = "Selectionnez une ligne pour afficher les debits theoriques x1 / x2 / x4 / x8 / x16.";

        pPcie.Controls.Add(lvPcie);
        pPcie.Controls.Add(lblTheory);
        pPcie.Controls.Add(bar2);

        // ---- onglet debit PCIe en direct --------------------------------
        // Le graphe est en Dock=Fill : il faut que l'onglet ait une taille
        // suffisante AVANT l'ajout des controles anches, sinon la zone
        // restante fait 0px (ou moins) et Chart rejette la hauteur.
        pNv.Size = new Size(1080, 640);

        Panel bar4 = new Panel();
        bar4.Dock = DockStyle.Top;
        bar4.Height = 34;
        btnNv.Text = "Demarrer la mesure";
        btnNv.SetBounds(8, 4, 150, 26);
        btnNv.Click += OnNvToggle;
        Label l4 = new Label();
        l4.Text = "Fenetre :";
        l4.SetBounds(170, 9, 50, 18);
        cmbNvWin.SetBounds(222, 6, 90, 24);
        cmbNvWin.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbNvWin.Items.AddRange(new object[] { "1 min", "5 min", "15 min", "30 min", "1 h" });
        cmbNvWin.SelectedIndex = 2;
        Button btnNvClear = new Button();
        btnNvClear.Text = "Effacer le graphe";
        btnNvClear.SetBounds(322, 4, 120, 26);
        btnNvClear.Click += delegate { nv.Clear(); nvPeak.Clear(); RedrawChart(); };
        lblNvStatus.Text = "Mesure du trafic PCIe en attente de demarrage.";
        lblNvStatus.SetBounds(452, 9, 640, 18);
        bar4.Controls.Add(btnNv);
        bar4.Controls.Add(l4);
        bar4.Controls.Add(cmbNvWin);
        bar4.Controls.Add(btnNvClear);
        bar4.Controls.Add(lblNvStatus);

        lvNv.Dock = DockStyle.Bottom;
        lvNv.Height = 150;
        lvNv.View = View.Details;
        lvNv.FullRowSelect = true;
        AddCols(lvNv, new string[][] {
            new string[] { "GPU", "60" },
            new string[] { "Carte graphique", "260" },
            new string[] { "Bus PCIe", "110" },
            new string[] { "Lien PCIe", "120" },
            new string[] { "rx PCIe instantane (Go/s)", "150" },
            new string[] { "tx PCIe instantane (Go/s)", "150" },
            new string[] { "rx max (Go/s)", "110" },
            new string[] { "tx max (Go/s)", "110" },
            new string[] { "rx moyen (Go/s)", "120" },
            new string[] { "tx moyen (Go/s)", "120" },
            new string[] { "Echantillons", "90" } });

        chart.Dock = DockStyle.Fill;
        ChartArea area = new ChartArea("trafic");
        area.AxisY.Title = "Go/s";
        area.AxisY.Minimum = 0;
        area.AxisY.LabelStyle.Format = "0.0";
        area.AxisX.Title = "heure";
        area.AxisX.LabelStyle.Format = "HH:mm:ss";
        area.AxisX.MajorGrid.LineColor = Color.Gainsboro;
        area.AxisY.MajorGrid.LineColor = Color.Gainsboro;
        chart.ChartAreas.Add(area);
        chart.Legends.Add(new Legend("legende"));
        chart.Legends[0].Docking = Docking.Bottom;
        chart.Legends[0].Alignment = StringAlignment.Center;
        Title chartTitle = new Title();
        chartTitle.Text = "Trafic PCIe reel de chaque carte graphique (rx = vers la carte, tx = depuis la carte)";
        chartTitle.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        chart.Titles.Add(chartTitle);

        pNv.Controls.Add(chart);
        pNv.Controls.Add(lvNv);
        pNv.Controls.Add(bar4);


        Panel bar3 = new Panel();
        bar3.Dock = DockStyle.Top;
        bar3.Height = 34;
        btnClear.Text = "Effacer l'historique";
        btnClear.SetBounds(8, 4, 150, 26);
        btnClear.Click += delegate { lvHist.Items.Clear(); };
        Label lh = new Label();
        lh.Text = "Enregistree a chaque changement de largeur/vitesse de liaison (x1, x4, x8, x16) ou de mesure.";
        lh.SetBounds(170, 9, 700, 18);
        bar3.Controls.Add(btnClear);
        bar3.Controls.Add(lh);

        lvHist.Dock = DockStyle.Fill;
        lvHist.View = View.Details;
        lvHist.FullRowSelect = true;
        AddCols(lvHist, new string[][] {
            new string[] { "Horodatage", "150" },
            new string[] { "Type", "80" },
            new string[] { "Peripherique", "320" },
            new string[] { "Etat observe", "300" },
            new string[] { "Debit", "110" } });

        pHist.Controls.Add(lvHist);
        pHist.Controls.Add(bar3);

        timer.Interval = 1000;
        timer.Tick += OnTick;
    }

    static void AddCols(ListView lv, object[][] cols)
    {
        foreach (object[] c in cols)
            lv.Columns.Add(Convert.ToString(c[0], CultureInfo.InvariantCulture),
                           Convert.ToInt32(c[1], CultureInfo.InvariantCulture));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshGpus();
        RefreshPcie(true);
        timer.Start();
        // demarrage automatique de la mesure du trafic PCIe reel
        if (NvTraffic.FindSmi() != null)
        {
            nv.Start();
            btnNv.Text = "Arreter la mesure";
        }
        lblNvStatus.Text = nv.Status;
        RedrawChart();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        nv.Stop();
        base.OnFormClosed(e);
    }

    void OnTick(object s, EventArgs e)
    {
        if (nv.IsRunning) RefreshNv();
        if (!chkAuto.Checked) return;
        RefreshPcie(false);
    }

    // ---- debit PCIe en direct ------------------------------------------

    void OnNvToggle(object s, EventArgs e)
    {
        if (nv.IsRunning)
        {
            nv.Stop();
            btnNv.Text = "Demarrer la mesure";
        }
        else
        {
            nv.Start();
            if (nv.IsRunning) btnNv.Text = "Arreter la mesure";
        }
        lblNvStatus.Text = nv.Status;
        RedrawChart();
    }

    int NvWindowSeconds()
    {
        switch (cmbNvWin.SelectedIndex)
        {
            case 0: return 60;
            case 1: return 300;
            case 3: return 1800;
            case 4: return 3600;
            default: return 900;
        }
    }

    // Rattache un GPU annonce par nvidia-smi a la carte PCIe correspondante.
    // L'index dmon n'est pas garanti identique a l'ordre WMI : on matche d'abord
    // par le nom, puis par l'ordre, puis quand il n'y a qu'une seule carte.
    Dictionary<int, PcieLink> NvLinks(List<NvGpu> gs)
    {
        Dictionary<int, PcieLink> res = new Dictionary<int, PcieLink>();
        List<GpuInfo> pool = new List<GpuInfo>();
        foreach (GpuInfo gi in gpus)
            if (gi.Link != null && gi.Link.HasLink) pool.Add(gi);

        foreach (NvGpu g in gs)
        {
            GpuInfo hit = null;
            foreach (GpuInfo gi in pool)
                if (string.Equals(gi.Name, g.Name, StringComparison.OrdinalIgnoreCase)) { hit = gi; break; }
            if (hit == null && g.Index >= 0 && g.Index < pool.Count) hit = pool[g.Index];
            // Dernier recours : une seule carte NVIDIA d'un cote comme de l'autre.
            if (hit == null && pool.Count == 1 && gs.Count == 1 &&
                (pool[0].IsHardware || g.Name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0))
                hit = pool[0];
            if (hit != null)
            {
                res[g.Index] = hit.Link;
                pool.Remove(hit);
            }
        }
        return res;
    }

    static bool ExistsNear(List<double> vals, double v)
    {
        foreach (double x in vals) if (Math.Abs(x - v) <= Math.Abs(v) * 0.01) return true;
        return false;
    }

    void RefreshNv()
    {
        lblNvStatus.Text = nv.Status;
        RedrawChart();

        List<NvGpu> gs = nv.Gpus();
        Dictionary<int, PcieLink> nl = NvLinks(gs);
        lvNv.BeginUpdate();
        lvNv.Items.Clear();
        // les valeurs sont deja converties en Go/s par NvTraffic.HandleLine
        foreach (NvGpu g in gs)
        {
            PcieLink lk;
            nl.TryGetValue(g.Index, out lk);
            ListViewItem it = new ListViewItem(g.Index.ToString(CultureInfo.InvariantCulture));
            it.SubItems.Add(g.Name);
            it.SubItems.Add(g.Bus);
            it.SubItems.Add(lk != null ? lk.LinkText() : "inconnu");
            it.SubItems.Add(g.LastRx.ToString("0.00", CultureInfo.InvariantCulture));
            it.SubItems.Add(g.LastTx.ToString("0.00", CultureInfo.InvariantCulture));
            it.SubItems.Add(g.MaxRx.ToString("0.00", CultureInfo.InvariantCulture));
            it.SubItems.Add(g.MaxTx.ToString("0.00", CultureInfo.InvariantCulture));
            it.SubItems.Add(g.Count > 0 ? (g.SumRx / g.Count).ToString("0.00", CultureInfo.InvariantCulture) : "-");
            it.SubItems.Add(g.Count > 0 ? (g.SumTx / g.Count).ToString("0.00", CultureInfo.InvariantCulture) : "-");
            it.SubItems.Add(g.Count.ToString(CultureInfo.InvariantCulture));
            lvNv.Items.Add(it);

            double peak = Math.Max(g.MaxRx, g.MaxTx);
            double old;
            nvPeak.TryGetValue(g.Index, out old);
            if (peak > old + 0.05)
            {
                nvPeak[g.Index] = peak;
                History.RecordMeasure(g.Name, "trafic PCIe rx/tx (pilote NVIDIA)",
                                      peak.ToString("0.00", CultureInfo.InvariantCulture) + " Go/s");
            }
        }
        lvNv.EndUpdate();
    }

    void RedrawChart()
    {
        List<NvGpu> gs = nv.Gpus();

        // Samples() attend un NOMBRE d'echantillons. "dmon -s t" produit
        // environ une ligne par carte et par seconde : la fenetre en secondes
        // doit donc etre multipliee par le nombre de cartes.
        int perGpu = Math.Max(1, gs.Count);
        List<NvSample> ss = nv.Samples(NvWindowSeconds() * perGpu);

        chart.Series.Clear();
        if (ss.Count == 0) return;

        // une serie par carte graphique, couleur fixe par index
        Dictionary<int, Series> map = new Dictionary<int, Series>();
        foreach (NvGpu g in gs)
        {
            string name = "GPU " + g.Index.ToString(CultureInfo.InvariantCulture) + " : " + g.Name;
            Series ser = new Series(name);
            ser.ChartType = SeriesChartType.Line;
            ser.BorderWidth = 2;
            ser.Color = NvColors[g.Index % NvColors.Length];
            chart.Series.Add(ser);
            map[g.Index] = ser;
        }

        // abscisse = heure reelle de chaque echantillon
        double dataMax = 0;
        List<int> plotted = new List<int>();
        foreach (NvSample smp in ss)
        {
            Series ser;
            if (!map.TryGetValue(smp.Index, out ser)) continue;
            // rx et tx sont deja en Go/s (conversion faite par HandleLine)
            double y = Math.Max(smp.Rx, smp.Tx);
            if (y > dataMax) dataMax = y;
            ser.Points.AddXY(smp.T.ToOADate(), y);
            if (!plotted.Contains(smp.Index)) plotted.Add(smp.Index);
        }

        // reperes : budget theorique du lien de CHAQUE carte tracee, et non le
        // lien le plus rapide de la machine. Une carte en x8 ne doit pas etre
        // comparee au plafond d'une carte en x16. L'echelle verticale est
        // etendue pour que le repere reste visible, mais seulement si le
        // budget reste du meme ordre de grandeur que le trafic mesure.
        chart.ChartAreas[0].AxisY.StripLines.Clear();
        double yMax = Math.Max(dataMax * 1.15, 1.0);
        Dictionary<int, PcieLink> nl = NvLinks(gs);
        List<double> drawn = new List<double>();
        foreach (KeyValuePair<int, PcieLink> kv in nl)
        {
            if (!plotted.Contains(kv.Key)) continue;
            double budget = kv.Value.CurrentGBs();
            if (budget <= 0 || budget > dataMax * 4.0) continue;
            // deux cartes au lien identique -> une seule ligne
            if (ExistsNear(drawn, budget)) continue;
            drawn.Add(budget);

            StripLine sl = new StripLine();
            sl.Interval = 0;
            sl.BorderWidth = 2;
            sl.BorderColor = NvColors[kv.Key % NvColors.Length];
            sl.BorderDashStyle = ChartDashStyle.Dash;
            sl.IntervalOffset = budget;
            sl.Text = "GPU " + kv.Key.ToString(CultureInfo.InvariantCulture) + "  " +
                      kv.Value.LinkText() + " = " +
                      budget.ToString("0.00", CultureInfo.InvariantCulture) + " Go/s";
            sl.TextAlignment = StringAlignment.Near;
            sl.TextLineAlignment = StringAlignment.Near;
            chart.ChartAreas[0].AxisY.StripLines.Add(sl);
            yMax = Math.Max(yMax, budget * 1.08);
        }
        chart.ChartAreas[0].AxisY.Maximum = yMax;

        // echelle horizontale : plage d'heures couverte par la fenetre.
        // Le pas d'affichage est choisi pour rester lisible selon la taille
        // de la fenetre (10 s, 1 min, 5 min ou 15 min entre deux graduations).
        int win = NvWindowSeconds();
        double step;
        if (win <= 120) step = 10;
        else if (win <= 900) step = 60;
        else if (win < 3600) step = 300;
        else step = 900;
        chart.ChartAreas[0].AxisX.IntervalType = DateTimeIntervalType.Seconds;
        chart.ChartAreas[0].AxisX.Interval = step;
        chart.ChartAreas[0].AxisX.Minimum = ss[0].T.ToOADate();
        chart.ChartAreas[0].AxisX.Maximum = ss[ss.Count - 1].T.ToOADate();
    }

    // ---- rafraichissement ----------------------------------------------
    public void RefreshGpus()
    {
        gpus = Gpus.Enumerate();
        lvGpu.Items.Clear();
        foreach (GpuInfo g in gpus)
        {
            string link;
            if (g.Link != null) link = g.Link.LinkText();
            else if (g.PnpId.StartsWith("SWD", StringComparison.OrdinalIgnoreCase)) link = "pilote logiciel (carte absente)";
            else if (g.PnpId.StartsWith("ACPI", StringComparison.OrdinalIgnoreCase)) link = "non reliee en PCIe (GPU du SoC)";
            else link = "non reliee en PCIe";
            string cur = g.Link == null ? "-" : g.Link.CurrentGBs().ToString("0.00", CultureInfo.InvariantCulture);
            string mx = g.Link == null ? "-" : g.Link.MaxGBs().ToString("0.00", CultureInfo.InvariantCulture);
            string diag = g.Error.Length > 0 ? g.Error : g.Chain;
            lvGpu.Items.Add(new ListViewItem(new string[] {
                g.Name, link, cur, mx,
                g.VramText(),
                g.DriverVersion, g.Compat, g.Resolution, diag, g.PnpId }));
        }
        UpdateNote();
    }

    // Le texte d'explication depend du resultat reel de l'enumeration : il est
    // redige apres chaque rafraichissement, jamais a la construction de la fenetre.
    void UpdateNote()
    {
        bool anyLink = false, anyHardware = false;
        if (gpus != null)
        {
            foreach (GpuInfo g in gpus)
            {
                if (g.IsHardware) anyHardware = true;
                if (g.Link != null && g.Link.HasLink) anyLink = true;
            }
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("LECTURE DE LA LIAISON PCI EXPRESS");
        sb.AppendLine();
        sb.AppendLine("Windows publie, pour tout peripherique PCI Express, les proprietes DEVPKEY_PciDevice_CurrentLinkSpeed et CurrentLinkWidth : la generation et le nombre de voies reellement negocies par le bus. La colonne \u00ab Liaison PCIe \u00bb affiche la premiere liaison trouvee pour la carte, soit son propre endpoint sur le bus, soit le port situe en amont. La colonne \u00ab Diagnostic \u00bb indique ce qui a ete trouve, ou l'etape qui a echoue.");
        sb.AppendLine();
        if (gpus == null)
        {
            sb.AppendLine("Enumeration en cours...");
        }
        else if (anyLink)
        {
            sb.AppendLine("Les cartes listees ci-dessus sont bien reliees a un port PCI Express : generation, largeur et debit theorique refletent la negociation reelle du bus. Une carte peut negocier moins que son maximum (emplacement du slot, carte mechaniquement limitee, economie d'energie) : comparez \u00ab Liaison PCIe \u00bb et \u00ab Debit max \u00bb, et consultez l'onglet \u00ab Historique \u00bb pour voir les changements de liaison.");
        }
        else if (anyHardware)
        {
            sb.AppendLine("Aucune liaison PCI Express lisible n'a ete trouvee pour les cartes listees. Lisez la colonne \u00ab Diagnostic \u00bb : soit Windows n'expose pas les proprietes PCIe de ce peripherique, soit la carte est vue a travers un pilote d'affichage indirect. L'onglet \u00ab Liaisons PCIe \u00bb liste toutes les liaisons que la machine publie : si la carte n'y apparait pas, c'est que Windows ne la rattache pas a un bus PCI.");
        }
        else
        {
            sb.AppendLine("Aucune carte graphique materielle n'a ete enumeree par WMI sur cette machine.");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("MESURE DU DEBIT REEL (Direct3D 11)");
        sb.AppendLine();
        sb.AppendLine("Le bouton \u00ab Mesurer les debits \u00bb chronometre trois copies d'une texture, cote GPU et cote CPU :");
        sb.AppendLine("  H2D : memoire systeme vers memoire de la carte (UpdateSubresource)");
        sb.AppendLine("  D2H : memoire de la carte vers memoire systeme (Map sur une texture STAGING)");
        sb.AppendLine("  D2D : copie GPU interne (CopyResource entre deux textures par defaut)");
        sb.AppendLine("La ligne \u00ab Reference CPU (memcpy) \u00bb sert de garde-fou : une mesure GPU superieure a plus de 2,5 fois ce reference signalerait une copie elidee par le pilote, donc non mesurable.");
        sb.AppendLine("Ces chiffres mesurent le chemin GPU<->systeme (le lien PCIe pour une carte discrete), pas la bande passante de la memoire video interne, qui necessiterait CUDA ou OpenCL.");
        sb.AppendLine("La mesure utilise l'adaptateur Direct3D par defaut de la session : sur une machine multi-cartes, elle ne concerne pas necessairement la carte affichee en premiere ligne.");
        sb.AppendLine("Direct3D 11 ne permet pas de memorie verrouillee (pinned) et ne fournit pas d'equivalent de cudaHostAlloc : le pilote copie deux fois (vers sa zone intermediaire puis vers la carte) et le chronometre s'arrete quand la copie est seulement SUBMISSIONNEE, pas terminee. Les chiffres D3D11 sont donc une borne basse. Pour le debit reel, utilisez l'onglet \u00ab Debit PCIe en direct \u00bb.");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("DEBIT PCIe EN DIRECT (valeurs mesurees par le pilote NVIDIA)");
        sb.AppendLine();
        sb.AppendLine("L'onglet \u00ab Debit PCIe en direct \u00bb ne fait aucune mesure manuelle : il lit la sortie de \u00ab nvidia-smi dmon -s t \u00bb, c'est-a-dire les compteurs du pilote NVIDIA qui comptent les octets effectivement echanges sur le lien PCI Express de chaque carte. Une courbe par carte, rx = trafic vers la carte, tx = trafic depuis la carte, en Go/s.");
        sb.AppendLine("C'est la mesure de reference sur une machine NVIDIA : elle compte tout le trafic du lien PCI Express de la carte (chargement des poids, transferts d'affichage, transferts vers un autre GPU par PCIe), y compris celui des programmes que vous lancez, par exemple un LLM. Le trafic NVLink n'est PAS compte ici : les colonnes NVLink (nvlrx/nvltx) sont volontairement ignorees, puisqu'elles ne passent pas par le port PCIe.");
        sb.AppendLine("Le parseur ne se fie jamais a la position des colonnes : il les reconnait par leur nom dans l'en-tete de \u00ab nvidia-smi \u00bb. Une version precedente lisait les colonnes de puissance et de temperature comme si c'etaient des debits, ce qui donnait des valeurs depassant le plafond du lien. Le programme rejoue sept mises en situation reelles : \u00ab GPU-Monitor.exe -SelfTest \u00bb ecrit un rapport SelfTest-date.txt a cote de l'executable (chercher la ligne \u00ab parseur : OK \u00bb), et \u00ab lancer-sans-exe.ps1 -SelfTest \u00bb l'affiche a l'ecran.");
        sb.AppendLine("Une ligne pointillee par carte represente le plafond theorique du lien de CETTE carte (generation x largeur, colonne \u00ab Lien PCIe \u00bb), et non celui de toute la machine. Elle n'apparait que si le trafic observe est du meme ordre de grandeur, pour ne pas ecraser les courbes.");
        sb.AppendLine("Le debit affiche peut legagerement depasser le plafond theorique : les compteurs du pilote NVIDIA sont echantillonnes sur une periode et arrondis, et le lien Gen4 peut monter au-dela de la valeur nominale une fois l'economie d'energie desactivee. Un depassement de quelques pour cent est normal ; un depassement important etait un bug de lecture des colonnes, corrige en version 1.4.");
        sb.AppendLine("L'axe horizontal du graphe affiche l'heure reelle de chaque echantillon (format hh:mm:ss), et non un nombre de secondes ecoulees depuis le debut de la mesure. L'espacement des graduations s'adapte a la fenetre choisie : 10 secondes pour 1 minute, 1 minute pour 5 ou 15 minutes, 5 minutes pour 30 minutes et 15 minutes pour 1 heure.");
        sb.AppendLine("Cet onglet necessite le pilote NVIDIA (nvidia-smi doit etre accessible). Sur une machine sans GPU NVIDIA, il reste vide et l'application fonctionne normalement avec les trois autres onglets.");
        txtNote.Text = sb.ToString();
    }

    public void RefreshPcie(bool full)
    {
        links = Pcie.Scan();
        if (full) lvPcie.Items.Clear();
        foreach (PcieLink l in links)
        {
            History.RecordLink(l.InstanceId, l.Name, l);
            ListViewItem it = lvPcie.Items.Find(l.InstanceId, false).Length > 0
                ? lvPcie.Items.Find(l.InstanceId, false)[0] : null;
            string[] cells = new string[] {
                l.Name, l.LinkText(), l.WidthText(),
                l.CurrentGBs().ToString("0.00", CultureInfo.InvariantCulture),
                l.HasMaxSpeed ? PcieLink.GenName((int)l.MaxSpeed) + " x" + l.MaxWidth.ToString(CultureInfo.InvariantCulture) : "n/a",
                l.HasMaxWidth ? l.MaxGBs().ToString("0.00", CultureInfo.InvariantCulture) : "n/a",
                l.InstanceId };
            if (it == null)
            {
                ListViewItem ni = new ListViewItem(cells);
                ni.Name = l.InstanceId;
                lvPcie.Items.Add(ni);
            }
            else
            {
                for (int i = 0; i < cells.Length; i++) it.SubItems[i].Text = cells[i];
            }
        }
        SyncHistoryView();
    }

    void SyncHistoryView()
    {
        List<HistEntry> snap = History.Snapshot();
        // L'historique est affiche du plus recent au plus ancien. On ne
        // reconstruit que si le contenu a change (sinon la selection de
        // l'utilisateur serait perdue a chaque tic d'horloge).
        if (lvHist.Items.Count == snap.Count && lvHist.Items.Count > 0 &&
            lvHist.Items[0].SubItems[0].Text ==
                snap[snap.Count - 1].T.ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture))
            return;
        lvHist.Items.Clear();
        for (int i = snap.Count - 1; i >= 0; i--)
        {
            HistEntry e = snap[i];
            lvHist.Items.Add(new ListViewItem(new string[] {
                e.T.ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture),
                e.Kind, e.Device, e.Detail, e.Value }));
        }
    }

    void OnPcieSelect(object s, EventArgs e)
    {
        if (lvPcie.SelectedIndices.Count == 0) return;
        int i = lvPcie.SelectedIndices[0];
        if (i >= links.Count) return;
        PcieLink l = links[i];
        StringBuilder sb = new StringBuilder();
        sb.Append(l.Name).Append("  -  liaison actuelle ").Append(l.LinkText()).Append(" = ")
          .Append(l.CurrentGBs().ToString("0.00", CultureInfo.InvariantCulture)).Append(" Go/s     ");
        for (int w = 1; w <= 16; w *= 2)
            sb.Append("x").Append(w.ToString(CultureInfo.InvariantCulture)).Append(" = ")
              .Append(PcieLink.GBs((int)l.CurSpeed, w).ToString("0.00", CultureInfo.InvariantCulture)).Append(" Go/s   ");
        lblTheory.Text = sb.ToString();
    }

    void OnBench(object s, EventArgs e)
    {
        int mb = int.Parse(cmbSize.SelectedItem.ToString().Split(' ')[0], CultureInfo.InvariantCulture);
        int reps = int.Parse(cmbReps.SelectedItem.ToString(), CultureInfo.InvariantCulture);
        btnBench.Enabled = false;
        lblBenchInfo.Text = "Initialisation de l'appareil Direct3D 11...";
        Application.DoEvents();
        try
        {
            bool ok = GpuBench.Ensure(1, mb);
            lblBenchInfo.Text = GpuBench.Status;
            lvBench.Items.Clear();
            if (ok)
            {
                lblBenchInfo.Text = "Mesure en cours...";
                Application.DoEvents();
                List<BenchRow> done = new List<BenchRow>();
                foreach (BenchRow r in GpuBench.Measure(reps))
                {
                    lvBench.Items.Add(new ListViewItem(new string[] {
                        r.Test,
                        r.Ok ? r.MinMs.ToString("0.000", CultureInfo.InvariantCulture) : "-",
                        r.Ok ? r.MedMs.ToString("0.000", CultureInfo.InvariantCulture) : "-",
                        r.Ok ? r.MaxMs.ToString("0.000", CultureInfo.InvariantCulture) : "-",
                        r.Ok ? r.GBs.ToString("0.00", CultureInfo.InvariantCulture) : "-",
                        r.Note }));
                    if (r.Ok) done.Add(r);
                }
                foreach (BenchRow r in done)
                {
                    History.RecordMeasure("GPU (materiel)",
                        r.Test + " / " + mb.ToString(CultureInfo.InvariantCulture) + " Mo",
                        r.GBs.ToString("0.00", CultureInfo.InvariantCulture) + " Go/s");
                }
                if (done.Count == 0)
                    History.RecordMeasure("GPU (materiel)",
                        mb.ToString(CultureInfo.InvariantCulture) + " Mo : aucune mesure exploitable", "");
                SyncHistoryView();
                lblBenchInfo.Text = "Mesure terminee a "
                    + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + " : " + done.Count.ToString(CultureInfo.InvariantCulture)
                    + " ligne(s) exploitable(s) sur " + mb.ToString(CultureInfo.InvariantCulture)
                    + " Mo. " + GpuBench.Status;
            }
        }
        catch (Exception ex)
        {
            lvBench.Items.Add(new ListViewItem(new string[] { "Exception", "-", "-", "-", "-", ex.Message }));
            lblBenchInfo.Text = "Erreur pendant la mesure : " + ex.Message;
        }
        finally
        {
            btnBench.Enabled = true;
        }
    }
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // GpuMonitor.exe -SelfTest : verifie le moteur sans fenetre et ecrit un
        // rapport SelfTest-<date>.txt a cote de l'executable (ou dans %TEMP%).
        if (args != null && args.Length > 0 &&
            (string.Equals(args[0], "-SelfTest", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(args[0], "/SelfTest", StringComparison.OrdinalIgnoreCase)))
        {
            SelfTest.Run();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
