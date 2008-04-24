//
// IpodSource.cs
//
// Author:
//   Aaron Bockover <abockover@novell.com>
//
// Copyright (C) 2005-2008 Novell, Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.IO;

using IPod;

using Hyena;
using Banshee.Base;
using Banshee.Dap;
using Banshee.Hardware;
using Banshee.Collection.Database;

namespace Banshee.Dap.Ipod
{
    public class IpodSource : DapSource
    {
        private PodSleuthDevice ipod_device;
        
        private string name_path;
        internal string NamePath {
            get { return name_path; }
        }
        
        private bool database_supported;
        internal bool DatabaseSupported {
            get { return database_supported; }
        }
        
#region Device Setup/Dispose
        
        public IpodSource (IDevice device) : base (device)
        {
            ipod_device = device as PodSleuthDevice;
            if (ipod_device == null) {
                throw new InvalidDeviceException ();
            }
        
            if (!LoadIpod ()) {
                throw new InvalidDeviceException ();
            }
            
            Initialize ();
        }
                
        public override void Dispose ()
        {
            base.Dispose ();
        }

        protected override void Eject ()
        {
            Dispose ();
        }
        
#endregion

#region Database Loading

        private bool LoadIpod ()
        {
            try {
                name_path = Path.Combine (Path.GetDirectoryName (ipod_device.TrackDatabasePath), "BansheeIPodName");
                
                if (File.Exists (ipod_device.TrackDatabasePath)) { 
                    ipod_device.LoadTrackDatabase ();
                } else {
                    int count = CountMusicFiles ();
                    Log.DebugFormat ("Found {0} files in /iPod_Control/Music", count);
                    if (CountMusicFiles () > 5) {
                        throw new DatabaseReadException ("No database, but found a lot of music files");
                    }
                }
                database_supported = true;
            } catch (DatabaseReadException) {
                ipod_device.LoadTrackDatabase (true);
                database_supported = false;
            } catch (Exception e) {
                Log.Exception (e);
                return false;
            }
            
            return true;
        }
        
        private int CountMusicFiles ()
        {
            try {
                int file_count = 0;
                
                DirectoryInfo m_dir = new DirectoryInfo (Path.Combine (ipod_device.ControlPath, "Music"));
                foreach (DirectoryInfo f_dir in m_dir.GetDirectories()) {
                    file_count += f_dir.GetFiles().Length;
                }
                
                return file_count;
            } catch {
                return 0;
            }
        }
        
#endregion

#region Source Cosmetics

        protected override string [] GetIconNames ()
        {
            string [] names = new string[4];
            string prefix = "multimedia-player-";
            string shell_color = ipod_device.ModelInfo.ShellColor;
            
            names[0] = ipod_device.ModelInfo.IconName;
            names[2] = "ipod-standard-color";
            names[3] = "multimedia-player";
            
            switch (ipod_device.ModelInfo.DeviceClass) {
                case "grayscale": 
                    names[1] = "ipod-standard-monochrome";
                    break;
                case "color": 
                    names[1] = "ipod-standard-color"; 
                    break;
                case "mini": 
                    names[1] = String.Format ("ipod-mini-{0}", shell_color);
                    names[2] = "ipod-mini-silver";
                    break;
                case "shuffle": 
                    names[1] = String.Format ("ipod-shuffle-{0}", shell_color);
                    names[2] = "ipod-shuffle";
                    break;
                case "nano":
                case "nano3":
                    names[1] = String.Format ("ipod-nano-{0}", shell_color);
                    names[2] = "ipod-nano-white";
                    break;
                case "video":
                    names[1] = String.Format ("ipod-video-{0}", shell_color);
                    names[2] = "ipod-video-white";
                    break;
                case "classic":
                case "touch":
                case "phone":
                default:
                    break;
            }
            
            names[1] = names[1] ?? names[2];
            names[1] = prefix + names[1];
            names[2] = prefix + names[2];
            
            foreach (string name in names) {
                Console.WriteLine (name);
            }
            
            return names;
        }
        
        public override void Rename (string name)
        {
            if (!CanRename) {
                return;
            }
        
            try {
                if (name_path != null) {
                    Directory.CreateDirectory (Path.GetDirectoryName (name_path));
                
                    using (StreamWriter writer = new StreamWriter (File.Open (name_path, FileMode.Create), 
                        System.Text.Encoding.Unicode)) {
                        writer.Write (name);
                    }
                }
            } catch (Exception e) {
                Log.Exception (e);
            }
            
            this.name = null;
            ipod_device.Name = name;
            base.Rename (name);
        }
        
        private string name;
        public override string Name {
            get {
                if (name != null) {
                    return name;
                }
                
                if (File.Exists (name_path)) {
                    using (StreamReader reader = new StreamReader (name_path, System.Text.Encoding.Unicode)) {
                        name = reader.ReadLine ();
                    }
                }
                
                if (String.IsNullOrEmpty (name)) {
                    name = ipod_device.Name;
                }
                    
                if (!String.IsNullOrEmpty (name)) {
                    return name;
                } else if (ipod_device.PropertyExists ("volume.label")) {
                    name = ipod_device.GetPropertyString ("volume.label");
                } else if (ipod_device.PropertyExists ("info.product")) {
                    name = ipod_device.GetPropertyString ("info.product");
                } else {
                    name = ((IDevice)ipod_device).Name ?? "iPod";
                }
                
                return name;
            }
        }
        
        public override bool CanRename {
            get { return !(IsAdding || IsDeleting || IsReadOnly); }
        }
        
        public override long BytesUsed {
            get { return (long)ipod_device.VolumeInfo.SpaceUsed; }
        }
        
        public override long BytesCapacity {
            get { return (long)ipod_device.VolumeInfo.Size; }
        }
        
#endregion

#region Syncing
        
        public override bool IsReadOnly {
            get { return ipod_device.IsReadOnly; }
        }
        
        public override void Import ()
        {
            Log.Information ("Import to Library is not implemented for iPods yet", true);
        }

        protected override void DeleteTrack (DatabaseTrackInfo track)
        {
        }
        
        protected override void OnTracksDeleted ()
        {
            base.OnTracksDeleted ();
        }

        protected override void AddTrackToDevice (DatabaseTrackInfo track, SafeUri fromUri)
        {
            if (track.PrimarySourceId == DbId) {
                return;
            }
        }

        /*private int OnUploadProgress (ulong sent, ulong total, IntPtr data)
        {
            AddTrackJob.DetailedProgress = (double) sent / (double) total;
            return 0;
        }*/

#endregion
        
    }
}
