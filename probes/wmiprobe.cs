using System;
using System.Management;
using System.Text;

public static class WP
{
    public static string Run()
    {
        StringBuilder o = new StringBuilder();
        ManagementObjectSearcher se = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
        ManagementObjectCollection col = se.Get();
        o.AppendLine("count=" + col.Count);
        foreach (ManagementObject mo in col)
        {
            o.AppendLine("--- obj ---");
            o.AppendLine("  __PATH=" + mo["__PATH"]);
            o.AppendLine("  props=" + mo.Properties.Count);
            string[] names = new string[] { "Name", "DeviceID", "DriverVersion", "DriverDate", "PNPClass", "AdapterRAM", "PNPDeviceID", "VideoProcessor", "Status" };
            foreach (string n in names)
            {
                try
                {
                    object v = mo[n];
                    o.AppendLine("  [" + n + "] = " + (v == null ? "(null)" : v.ToString()));
                }
                catch (Exception ex)
                {
                    o.AppendLine("  [" + n + "] ERREUR " + ex.GetType().Name + " : " + ex.Message);
                }
            }
            foreach (PropertyData pd in mo.Properties)
            {
                string val = "";
                try { val = pd.Value == null ? "(null)" : pd.Value.ToString(); }
                catch (Exception ex) { val = "ERREUR " + ex.GetType().Name + " : " + ex.Message; }
                o.AppendLine("  P " + pd.Name + " = " + val);
            }
        }
        return o.ToString();
    }
}
