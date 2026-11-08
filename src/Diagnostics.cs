using System;
using System.IO;
using System.Text;

namespace Ncp
{
    static class Diagnostics
    {
        public static int Run()
        {
            var log=new StringBuilder();int code=0;
            log.AppendLine("NCP read-only NVIDIA diagnostics");log.AppendLine("UTC: "+DateTime.UtcNow.ToString("o"));
            log.AppendLine("Version: "+typeof(Program).Assembly.GetName().Version+"; Windows: "+Environment.OSVersion);
            log.AppendLine("ICC API: "+(DisplayIccAssociations.Available?"display ColorProfile":"legacy WCS"));
            log.AppendLine("No color setters, profile writes, game access, or Control Panel launch.");
            try {
                using(var nv=new NvidiaBackend()) {
                    var displays=nv.Enumerate();log.AppendLine("Displays: "+displays.Count);
                    foreach(var d in displays) {
                        log.AppendLine(d.ToString());
                        try {
                            var r=nv.Read(d);log.AppendLine("Vibrance="+r.Vibrance+", Hue="+r.Hue);
                            log.AppendLine("Panel saved values available="+(r.PanelValues!=null));
                            if(r.PanelReadIssue!=null)log.AppendLine(r.PanelReadIssue);
                            if(r.PanelValues!=null)log.AppendLine("Panel saved B/C/G="+r.PanelValues.Brightness+"/"+r.PanelValues.Contrast+"/"+r.PanelValues.Gamma);
                            log.AppendLine("Live gamma readback: unsupported. Setters: resolved, not invoked.");
                            try {
                                var icc=new WindowsIccBackend().Read(d);log.AppendLine("Windows ICC: "+icc.Fingerprint);
                                log.AppendLine("Associated ICCs: "+string.Join(", ",icc.Profiles??new System.Collections.Generic.List<string>()));
                                log.AppendLine(WindowsIccBackend.DescribeTarget(d));
                            }catch(Exception e) {log.AppendLine("Windows ICC read: "+e.Message);}
                        } catch(Exception e) {code=1;log.AppendLine(e.ToString());}
                    }
                    if(displays.Count==0)code=1;
                }
            }catch(Exception e){code=1;log.AppendLine(e.ToString());}
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"diagnostics.txt"),log.ToString(),Encoding.UTF8);return code;
        }
        internal static void RecordIccFailure(DisplayTarget display,string requested,Exception error)
        {
            // Capture before Controller rolls back. Keep only the latest failure.
            try {
                var log=new StringBuilder();log.AppendLine("NCP ICC application failure (before rollback)");
                log.AppendLine("UTC: "+DateTime.UtcNow.ToString("o"));
                log.AppendLine("Version: "+typeof(Program).Assembly.GetName().Version+"; Windows: "+Environment.OSVersion);
                log.AppendLine("ICC API: "+(DisplayIccAssociations.Available?"display ColorProfile":"legacy WCS"));
                log.AppendLine("Display: "+display.GdiName+"; interface: "+display.Key);log.AppendLine("Requested: "+requested);
                log.AppendLine(error.ToString());
                try {log.AppendLine(WindowsIccBackend.DescribeTarget(display));}catch(Exception e) {log.AppendLine(e.Message);}
                string text=log.ToString();if(text.Length>65536)text=text.Substring(0,65536);
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"icc-error.log"),text,Encoding.UTF8);
            }catch { }
        }
    }
}
