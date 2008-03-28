//
// BansheeDbFormatMigrator.cs
//
// Author:
//   Aaron Bockover <abockover@novell.com>
//
// Copyright (C) 2007-2008 Novell, Inc.
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
using System.Data;
using System.Reflection;
using System.Threading;
using Mono.Unix;

using Hyena;
using Hyena.Data.Sqlite;
using Timer=Hyena.Timer;

using Banshee.ServiceStack;
using Banshee.Sources;
using Banshee.Collection;
using Banshee.Collection.Database;
using Banshee.Streaming;

namespace Banshee.Database
{
    public class BansheeDbFormatMigrator
    {
        
#region Migration Driver
        
        public delegate void SlowStartedHandler(string title, string message);
        
        public event SlowStartedHandler SlowStarted;
        public event EventHandler SlowPulse;
        public event EventHandler SlowFinished;

        public event EventHandler Started;
        public event EventHandler Finished;
        
        // NOTE: Whenever there is a change in ANY of the database schema,
        //       this version MUST be incremented and a migration method
        //       MUST be supplied to match the new version number
        protected const int CURRENT_VERSION = 5;
        
        protected class DatabaseVersionAttribute : Attribute 
        {
            private int version;
            
            public DatabaseVersionAttribute(int version)
            {
                this.version = version;
            }
            
            public int Version {
                get { return version; }
            }
        }
        
        private HyenaSqliteConnection connection;
        
        public BansheeDbFormatMigrator (HyenaSqliteConnection connection)
        {
            this.connection = connection;
        }
        
        protected virtual void OnSlowStarted(string title, string message)
        {
            SlowStartedHandler handler = SlowStarted;
            if(handler != null) {
                handler(title, message);
            }
        }
        
        protected virtual void OnSlowPulse()
        {
            EventHandler handler = SlowPulse;
            if(handler != null) {
                handler(this, EventArgs.Empty);
            }
        }
        
        protected virtual void OnSlowFinished()
        {
            EventHandler handler = SlowFinished;
            if(handler != null) {
                handler(this, EventArgs.Empty);
            }
        }

        protected virtual void OnStarted ()
        {
            EventHandler handler = Started;
            if (handler != null) {
                handler (this, EventArgs.Empty);
            }
        }

        protected virtual void OnFinished ()
        {
            EventHandler handler = Finished;
            if (handler != null) {
                handler (this, EventArgs.Empty);
            }
        }
        
        public void Migrate ()
        {
            try {
                Execute ("BEGIN");
                InnerMigrate ();
                Execute ("COMMIT");
            } catch (Exception e) {
                Console.WriteLine ("Rolling back transaction");
                Console.WriteLine (e);
                Execute ("ROLLBACK");
            }

            OnFinished ();
        }
        
        private void InnerMigrate ()
        {
            MethodInfo [] methods = GetType ().GetMethods (BindingFlags.Instance | BindingFlags.NonPublic);
            bool terminate = false;
            bool ran_migration_step = false;
            
            for (int i = DatabaseVersion + 1; i <= CURRENT_VERSION; i++) {
                foreach (MethodInfo method in methods) {
                    foreach (DatabaseVersionAttribute attr in method.GetCustomAttributes (
                        typeof (DatabaseVersionAttribute), false)) {
                        if (attr.Version != i) {
                            continue;
                        }
                        
                        if (!ran_migration_step) {
                            ran_migration_step = true;
                            OnStarted ();
                        }

                        if (!(bool)method.Invoke (this, null)) {
                            terminate = true;
                        }
                        
                        break;
                    }
                }
                
                if (terminate) {
                    break;
                }
            }
            
            Execute (String.Format ("UPDATE CoreConfiguration SET Value = {0} WHERE Key = 'DatabaseVersion'", CURRENT_VERSION));
        }
        
        protected bool TableExists(string tableName)
        {
            return connection.TableExists (tableName);
        }
        
        protected void Execute(string query)
        {
            connection.Execute (query);
        }
            
        protected int DatabaseVersion {
            get {
                if (!TableExists("CoreConfiguration")) {
                    return 0;
                }
                
                return connection.Query<int> ("SELECT Value FROM CoreConfiguration WHERE Key = 'DatabaseVersion'");
            }
        }
        
#endregion
        
#region Migration Step Implementations
#pragma warning disable 0169
        // NOTE: Return true if the step should allow the driver to continue
        //       Return false if the step should terminate driver
        
        [DatabaseVersion (1)]
        private bool Migrate_1 ()
        {
            if (TableExists("Tracks")) {
                InitializeFreshDatabase ();
                
                uint timer_id = Log.DebugTimerStart ("Database Schema Migration");

                OnSlowStarted (Catalog.GetString ("Upgrading your Banshee Database"), 
                    Catalog.GetString ("Please wait while your old Banshee database is migrated to the new format."));
            
                Thread thread = new Thread (MigrateFromLegacyBanshee);
                thread.Start ();
            
                while (thread.IsAlive) {
                    OnSlowPulse ();
                    Thread.Sleep (100);
                }

                Log.DebugTimerPrint (timer_id);
            
                OnSlowFinished ();
                
                return false;
            } else {
                InitializeFreshDatabase ();
                return false;
            }
        }
        
        [DatabaseVersion (2)]
        private bool Migrate_2 ()
        {
            Execute (String.Format ("ALTER TABLE CoreTracks ADD COLUMN Attributes INTEGER  DEFAULT {0}",
                (int)TrackMediaAttributes.Default));
            return true;
        }

        [DatabaseVersion (3)]
        private bool Migrate_3 ()
        {
            Execute ("ALTER TABLE CorePlaylists ADD COLUMN PrimarySourceID INTEGER");
            Execute ("UPDATE CorePlaylists SET PrimarySourceID = 1");

            Execute ("ALTER TABLE CoreSmartPlaylists ADD COLUMN PrimarySourceID INTEGER");
            Execute ("UPDATE CoreSmartPlaylists SET PrimarySourceID = 1");
            return true;
        }

        [DatabaseVersion (4)]
        private bool Migrate_4 ()
        {
            Execute ("ALTER TABLE CoreTracks ADD COLUMN LastSkippedStamp INTEGER");
            return true;
        }
        
        [DatabaseVersion (5)]
        private bool Migrate_5 ()
        {
            Execute ("ALTER TABLE CoreTracks ADD COLUMN TitleLowered TEXT");
            Execute ("ALTER TABLE CoreArtists ADD COLUMN NameLowered TEXT");
            Execute ("ALTER TABLE CoreAlbums ADD COLUMN TitleLowered TEXT");

            // Set default so sorting isn't whack while we regenerate
            Execute ("UPDATE CoreTracks SET TitleLowered = lower(Title)");
            Execute ("UPDATE CoreArtists SET NameLowered = lower(Name)");
            Execute ("UPDATE CoreAlbums SET TitleLowered = lower(Title)");

            // Drop old indexes
            Execute ("DROP INDEX IF EXISTS CoreTracksPrimarySourceIndex");
            Execute ("DROP INDEX IF EXISTS CoreTracksArtistIndex");
            Execute ("DROP INDEX IF EXISTS CoreTracksAlbumIndex");
            Execute ("DROP INDEX IF EXISTS CoreTracksRatingIndex");
            Execute ("DROP INDEX IF EXISTS CoreTracksLastPlayedStampIndex");
            Execute ("DROP INDEX IF EXISTS CoreTracksDateAddedStampIndex");
            Execute ("DROP INDEX IF EXISTS CoreTracksPlayCountIndex");
            Execute ("DROP INDEX IF EXISTS CoreTracksTitleIndex");
            Execute ("DROP INDEX IF EXISTS CoreAlbumsIndex");
            Execute ("DROP INDEX IF EXISTS CoreAlbumsArtistID");
            Execute ("DROP INDEX IF EXISTS CoreArtistsIndex");
            Execute ("DROP INDEX IF EXISTS CorePlaylistEntriesIndex");
            Execute ("DROP INDEX IF EXISTS CorePlaylistTrackIDIndex");
            Execute ("DROP INDEX IF EXISTS CoreSmartPlaylistEntriesPlaylistIndex");
            Execute ("DROP INDEX IF EXISTS CoreSmartPlaylistEntriesTrackIndex");

            // Create new indexes
            Execute ("CREATE INDEX IF NOT EXISTS CoreTracksIndex ON CoreTracks(ArtistID, AlbumID, PrimarySourceID, Disc, TrackNumber, Uri)");
            Execute ("CREATE INDEX IF NOT EXISTS CoreArtistsIndex ON CoreArtists(NameLowered)");
            Execute ("CREATE INDEX IF NOT EXISTS CoreAlbumsIndex       ON CoreAlbums(ArtistID, TitleLowered)");
            Execute ("CREATE INDEX IF NOT EXISTS CoreSmartPlaylistEntriesIndex ON CoreSmartPlaylistEntries(SmartPlaylistID, TrackID)");
            Execute ("CREATE INDEX IF NOT EXISTS CorePlaylistEntriesIndex ON CorePlaylistEntries(PlaylistID, TrackID)");
            
            refresh_from_file = false;
            ServiceManager.ServiceStarted += OnServiceStarted;
            
            return true;
        }
        
#pragma warning restore 0169
        
        private void InitializeFreshDatabase()
        {
            Execute("DROP TABLE IF EXISTS CoreConfiguration");
            Execute("DROP TABLE IF EXISTS CoreTracks");
            Execute("DROP TABLE IF EXISTS CoreArtists");
            Execute("DROP TABLE IF EXISTS CoreAlbums");
            Execute("DROP TABLE IF EXISTS CorePlaylists");
            Execute("DROP TABLE IF EXISTS CorePlaylistEntries");
            Execute("DROP TABLE IF EXISTS CoreSmartPlaylists");
            Execute("DROP TABLE IF EXISTS CoreSmartPlaylistEntries");
            Execute("DROP TABLE IF EXISTS CoreRemovedTracks");
            Execute("DROP TABLE IF EXISTS CoreTracksCache");
            Execute("DROP TABLE IF EXISTS CoreCache");
            
            Execute(@"
                CREATE TABLE CoreConfiguration (
                    EntryID             INTEGER PRIMARY KEY,
                    Key                 TEXT,
                    Value               TEXT
                )
            ");
            Execute (String.Format ("INSERT INTO CoreConfiguration VALUES (null, 'DatabaseVersion', {0})", CURRENT_VERSION));
            
            Execute(@"
                CREATE TABLE CorePrimarySources (
                    PrimarySourceID     INTEGER PRIMARY KEY,
                    StringID            TEXT UNIQUE
                )
            ");
            Execute ("INSERT INTO CorePrimarySources (StringID) VALUES ('Library')");

            // TODO add these:
            // Others to consider:
            // AlbumArtist (TPE2) (in CoreAlbums?)
            Execute(String.Format (@"
                CREATE TABLE CoreTracks (
                    PrimarySourceID     INTEGER NOT NULL,
                    TrackID             INTEGER PRIMARY KEY,
                    ArtistID            INTEGER,
                    AlbumID             INTEGER,
                    TagSetID            INTEGER,
                    
                    MusicBrainzID       TEXT,

                    Uri                 TEXT,
                    UriType             INTEGER,
                    MimeType            TEXT,
                    FileSize            INTEGER,
                    Attributes          INTEGER DEFAULT {0},
                    
                    Title               TEXT,
                    TitleLowered        TEXT,
                    TrackNumber         INTEGER,
                    TrackCount          INTEGER,
                    Disc                INTEGER,
                    Duration            INTEGER,
                    Year                INTEGER,
                    Genre               TEXT,
                    Composer            TEXT,
                    Copyright           TEXT,
                    LicenseUri          TEXT,

                    Comment             TEXT,
                    Rating              INTEGER,
                    PlayCount           INTEGER,
                    SkipCount           INTEGER,
                    LastPlayedStamp     INTEGER,
                    LastSkippedStamp    INTEGER,
                    DateAddedStamp      INTEGER,
                    DateUpdatedStamp    INTEGER
                )
            ", (int)TrackMediaAttributes.Default));
            Execute("CREATE INDEX CoreTracksPrimarySourceIndex ON CoreTracks(ArtistID, AlbumID, PrimarySourceID, Disc, TrackNumber, Uri)");
            Execute("CREATE INDEX CoreTracksAggregatesIndex ON CoreTracks(FileSize, Duration)");
            
            Execute(@"
                CREATE TABLE CoreAlbums (
                    AlbumID             INTEGER PRIMARY KEY,
                    ArtistID            INTEGER,
                    TagSetID            INTEGER,
                    
                    MusicBrainzID       TEXT,

                    Title               TEXT,
                    TitleLowered        TEXT,

                    ReleaseDate         INTEGER,
                    Duration            INTEGER,
                    Year                INTEGER,
                    
                    Rating              INTEGER
                )
            ");
            Execute("CREATE INDEX CoreAlbumsIndex       ON CoreAlbums(ArtistID, TitleLowered)");

            Execute(@"
                CREATE TABLE CoreArtists (
                    ArtistID            INTEGER PRIMARY KEY,
                    TagSetID            INTEGER,
                    MusicBrainzID       TEXT,
                    Name                TEXT,
                    NameLowered         TEXT,
                    Rating              INTEGER
                )
            ");
            Execute("CREATE INDEX CoreArtistsIndex ON CoreArtists(NameLowered)");
            
            Execute(@"
                CREATE TABLE CorePlaylists (
                    PrimarySourceID     INTEGER,
                    PlaylistID          INTEGER PRIMARY KEY,
                    Name                TEXT,
                    SortColumn          INTEGER NOT NULL DEFAULT -1,
                    SortType            INTEGER NOT NULL DEFAULT 0,
                    Special             INTEGER NOT NULL DEFAULT 0
                )
            ");
            
            Execute(@"
                CREATE TABLE CorePlaylistEntries (
                    EntryID             INTEGER PRIMARY KEY,
                    PlaylistID          INTEGER NOT NULL,
                    TrackID             INTEGER NOT NULL,
                    ViewOrder           INTEGER NOT NULL DEFAULT 0
                )
            ");
            Execute("CREATE INDEX CorePlaylistEntriesIndex ON CorePlaylistEntries(PlaylistID, TrackID)");
            
            Execute(@"
                CREATE TABLE CoreSmartPlaylists (
                    PrimarySourceID     INTEGER,
                    SmartPlaylistID     INTEGER PRIMARY KEY,
                    Name                TEXT NOT NULL,
                    Condition           TEXT,
                    OrderBy             TEXT,
                    LimitNumber         TEXT,
                    LimitCriterion      TEXT
                )
            ");
                
            Execute(@"
                CREATE TABLE CoreSmartPlaylistEntries (
                    EntryID             INTEGER PRIMARY KEY,
                    SmartPlaylistID     INTEGER NOT NULL,
                    TrackID             INTEGER NOT NULL
                )
            ");
            Execute("CREATE INDEX CoreSmartPlaylistEntriesPlaylistIndex ON CoreSmartPlaylistEntries(SmartPlaylistID, TrackID)");

            Execute(@"
                CREATE TABLE CoreRemovedTracks (
                    TrackID             INTEGER NOT NULL,
                    Uri                 TEXT,
                    DateRemovedStamp    INTEGER
                )
            ");

            Execute(@"
                CREATE TABLE CoreCacheModels (
                    CacheID             INTEGER PRIMARY KEY,
                    ModelID             TEXT
                )
            ");
            
            Execute(@"
                CREATE TABLE CoreCache (
                    OrderID             INTEGER PRIMARY KEY,
                    ModelID             INTEGER,
                    ItemID              INTEGER
                )
            ");

            // This index slows down queries were we shove data into the CoreCache.
            // Since we do that frequently, not using it.
            //Execute("CREATE INDEX CoreCacheModelId      ON CoreCache(ModelID)");
        }
        
        private void MigrateFromLegacyBanshee()
        {
            Thread.Sleep (3000);
            Execute(@"
                INSERT INTO CoreArtists 
                    SELECT DISTINCT null, 0, null, Artist, NULL, 0 
                        FROM Tracks 
                        ORDER BY Artist
            ");
            
            Execute(@"
                INSERT INTO CoreAlbums
                    SELECT DISTINCT null,
                        (SELECT ArtistID 
                            FROM CoreArtists 
                            WHERE Name = Tracks.Artist
                            LIMIT 1),
                        0, null, AlbumTitle, NULL, ReleaseDate, 0, 0, 0
                        FROM Tracks
                        ORDER BY AlbumTitle
            ");
            
            Execute (String.Format (@"
                INSERT INTO CoreTracks
                    SELECT 
                        1,
                        TrackID, 
                        (SELECT ArtistID 
                            FROM CoreArtists 
                            WHERE Name = Artist),
                        (SELECT a.AlbumID 
                            FROM CoreAlbums a, CoreArtists b
                            WHERE a.Title = AlbumTitle 
                                AND a.ArtistID = b.ArtistID
                                AND b.Name = Artist),
                        0,
                        0,
                        Uri,
                        0,
                        MimeType,
                        0,
                        {0},
                        Title, NULL,
                        TrackNumber,
                        TrackCount,
                        0,
                        Duration * 1000,
                        Year,
                        Genre,
                        NULL, NULL, NULL, NULL,
                        Rating,
                        NumberOfPlays,
                        0,
                        LastPlayedStamp,
                        NULL,
                        DateAddedStamp,
                        DateAddedStamp
                        FROM Tracks
            ", (int)TrackMediaAttributes.Default));

            Execute ("update coretracks set lastplayedstamp = NULL where lastplayedstamp = -62135575200");

            Execute(@"
                INSERT INTO CorePlaylists (PlaylistID, Name, SortColumn, SortType)
                    SELECT * FROM Playlists
            ");

            Execute(@"
                INSERT INTO CorePlaylistEntries
                    SELECT * FROM PlaylistEntries
            ");

            Execute(@"
                INSERT INTO CoreSmartPlaylists (SmartPlaylistID, Name, Condition, OrderBy, LimitNumber, LimitCriterion)
                    SELECT * FROM SmartPlaylists
            ");

            Execute ("UPDATE CoreSmartPlaylists SET PrimarySourceID = 1");
            Execute ("UPDATE CorePlaylists SET PrimarySourceID = 1");

            refresh_from_file = true;
            ServiceManager.ServiceStarted += OnServiceStarted;
        }

        private void OnServiceStarted (ServiceStartedArgs args)
        {
            if (args.Service is UserJobManager) {
                ServiceManager.ServiceStarted -= OnServiceStarted;
                if (ServiceManager.SourceManager.MusicLibrary != null) {
                    RefreshMetadataDelayed ();
                } else {
                    ServiceManager.SourceManager.SourceAdded += OnSourceAdded;
                }
            }
        }

        private void OnSourceAdded (SourceAddedArgs args)
        {
            if (ServiceManager.SourceManager.MusicLibrary != null && ServiceManager.SourceManager.VideoLibrary != null) {
                ServiceManager.SourceManager.SourceAdded -= OnSourceAdded;
                RefreshMetadataDelayed ();
            }
        }

        private void RefreshMetadataDelayed ()
        {
            Application.RunTimeout (3000, RefreshMetadata);
        }

        private bool RefreshMetadata ()
        {
            ThreadPool.QueueUserWorkItem (RefreshMetadataThread);
            return false;
        }

        private bool refresh_from_file = false;
        private void RefreshMetadataThread (object state)
        {
            int total = ServiceManager.DbConnection.Query<int> ("SELECT count(*) FROM CoreTracks");

            if (total <= 0) {
                return;
            }

            UserJob job = new UserJob (Catalog.GetString ("Refreshing Metadata"));
            job.Status = Catalog.GetString ("Scanning...");
            job.IconNames = new string [] { "system-search", "gtk-find" };
            job.Register ();

            HyenaSqliteCommand select_command = new HyenaSqliteCommand (
                String.Format (
                    "SELECT {0} FROM {1} WHERE {2}",
                    DatabaseTrackInfo.Provider.Select,
                    DatabaseTrackInfo.Provider.From,
                    DatabaseTrackInfo.Provider.Where
                )
            );

            int count = 0;
            using (System.Data.IDataReader reader = ServiceManager.DbConnection.Query (select_command)) {
                while (reader.Read ()) {
                    DatabaseTrackInfo track = DatabaseTrackInfo.Provider.Load (reader, 0);
                    
                    try {
                        if (refresh_from_file) {
                            TagLib.File file = StreamTagger.ProcessUri (track.Uri);
                            StreamTagger.TrackInfoMerge (track, file, true);
                        }
                    } catch (Exception e) {
                        Log.Warning (String.Format ("Failed to update metadata for {0}", track),
                            e.GetType ().ToString (), false);
                    }
                    
                    track.Save (false);
                    track.Artist.Save ();
                    track.Album.Save ();

                    job.Status = String.Format ("{0} - {1}", track.DisplayArtistName, track.DisplayTrackTitle);
                    job.Progress = (double)++count / (double)total;
                }
            }

            job.Finish ();
            ServiceManager.SourceManager.MusicLibrary.NotifyTracksChanged ();
        }
        
#endregion

    }
}
