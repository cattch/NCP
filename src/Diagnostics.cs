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
                            try {var icc=new WindowsIccBackend().Read(d);log.AppendLine("Windows ICC: "+icc.Fingerprint);}catch(Exception e) {log.AppendLine("Windows ICC read: "+e.Message);}
                        } catch(Exception e) {code=1;log.AppendLine(e.ToString());}
                    }
                    if(displays.Count==0)code=1;
                }
            }catch(Exception e){code=1;log.AppendLine(e.ToString());}
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"diagnostics.txt"),log.ToString(),Encoding.UTF8);return code;
        }
    }
}
