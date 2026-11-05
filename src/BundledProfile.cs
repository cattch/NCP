using System;
using System.Collections.Generic;
using System.IO;

namespace Ncp
{
    static class BundledProfile
    {
        internal static AppData Create(string programDirectory)
        {
            var profile=Load(new IccLibrary(programDirectory));
            return new AppData {
                Profiles=new List<PresetProfile>{profile},ActiveProfileId=profile.Id,
                Presets=profile.Presets,PresetOrder=profile.Order
            };
        }
        internal static PresetProfile Load(IccLibrary library)
        {
            using(var stream=typeof(BundledProfile).Assembly.GetManifestResourceStream("Ncp.DefaultProfile")) {
                if(stream==null)throw new InvalidDataException("내장 기본 프로필을 찾을 수 없습니다.");
                return SharePackages.ImportProfile(stream,library);
            }
        }
    }
}
