using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;

namespace RaceLib
{
    public interface IDatabase : IDisposable
    {
        int Version { get; }

        IEnumerable<SimpleEvent> GetSimpleEvents();

        void Init(Guid eventId);

        Event LoadEvent();
        IEnumerable<Race> LoadRaces();
        IEnumerable<Result> LoadResults();

        bool Insert<T>(T t) where T : BaseObject, new();
        bool Insert<T>(IEnumerable<T> t) where T : BaseObject, new();
        
        bool Update<T>(T t) where T : BaseObject, new();
        bool Update<T>(IEnumerable<T> t) where T : BaseObject, new();
        
        bool Upsert<T>(T t) where T : BaseObject, new();
        bool Upsert<T>(IEnumerable<T> t) where T : BaseObject, new();

        bool Delete<T>(T t) where T : BaseObject, new();
        bool Delete<T>(IEnumerable<T> t) where T : BaseObject, new();

        IEnumerable<T> All<T>() where T : BaseObject, new();

        T GetCreateExternalObject<T>(int externalId) where T : BaseObject, new();
        T GetCreateObject<T>(Guid id) where T : BaseObject, new();

        T GetObject<T>(Guid id) where T : BaseObject, new();
        IEnumerable<T> GetObjects<T>(IEnumerable<Guid> ids) where T : BaseObject, new();

        // The revision of a race as saved, without loading all of it. False if it isn't saved.
        bool GetSavedRaceRevision(Guid raceId, out int revision, out string savedBy);
    }

    public interface IDatabaseFactory
    {
        IDatabase Open(Guid eventId);
    }

    // Gets a say in every write to an event's database. Used by multi-PC review to stop one PC
    // overwriting races another PC has open or has saved since.
    public interface IDatabaseWriteGuard
    {
        Guid EventId { get; }

        bool Guards(Type type);

        // Calls write, unless the guard refuses it. Returns whether the object was written.
        bool Write(IDatabase database, BaseObject obj, Func<bool> write);
    }

    public static class DatabaseFactory
    {
        private static IDatabaseFactory databaseFactory;

        public static IDatabaseWriteGuard WriteGuard { get; set; }

        public static void Init(IDatabaseFactory dbf)
        {
            databaseFactory = dbf;
        }

        public static IDatabase Open()
        {
            return Open(Guid.Empty);
        }

        public static IDatabase Open(Guid eventId)
        {
            IDatabaseWriteGuard writeGuard = WriteGuard;
            if (writeGuard != null && writeGuard.EventId != eventId)
            {
                writeGuard = null;
            }

            return new DatabaseErrorWrapper(databaseFactory.Open(eventId), writeGuard);
        }
    }

    public class DatabaseErrorWrapper : IDatabase
    {
        private IDatabase database;
        private IDatabaseWriteGuard writeGuard;

        public int Version
        {
            get
            {
                return database.Version;
            }
        }

        public DatabaseErrorWrapper(IDatabase database, IDatabaseWriteGuard writeGuard = null)
        {
            this.database = database;
            this.writeGuard = writeGuard;
        }

        public IEnumerable<T> All<T>() where T : BaseObject, new()
        {
            return database.All<T>();
        }

        public void Dispose()
        {
            database?.Dispose();
            database = null;
        }

        public T GetCreateExternalObject<T>(int externalId) where T : BaseObject, new()
        {
            return database.GetCreateExternalObject<T>(externalId);
        }

        public T GetCreateObject<T>(Guid id) where T : BaseObject, new()
        {
            return database.GetCreateObject<T>(id);
        }

        public T GetObject<T>(Guid id) where T : BaseObject, new()
        {
            return database.GetObject<T>(id);
        }

        public IEnumerable<T> GetObjects<T>(IEnumerable<Guid> ids) where T : BaseObject, new()
        {
            return database.GetObjects<T>(ids);
        }

        public IEnumerable<SimpleEvent> GetSimpleEvents()
        {
            return database.GetSimpleEvents();
        }

        public void Init(Guid eventId)
        {
            database.Init(eventId);
        }

        public Event LoadEvent()
        {
            return database.LoadEvent();
        }

        public IEnumerable<Race> LoadRaces()
        {
            return database.LoadRaces();
        }

        public IEnumerable<Result> LoadResults()
        {
            return database.LoadResults();
        }

        public bool GetSavedRaceRevision(Guid raceId, out int revision, out string savedBy)
        {
            return database.GetSavedRaceRevision(raceId, out revision, out savedBy);
        }

        private bool Write(BaseObject obj, Func<bool> write)
        {
            if (writeGuard == null || obj == null || !writeGuard.Guards(obj.GetType()))
            {
                return write();
            }

            return writeGuard.Write(database, obj, write);
        }

        private bool WriteAll<T>(IEnumerable<T> objs, Func<IEnumerable<T>, bool> write) where T : BaseObject
        {
            if (writeGuard == null || objs == null || !writeGuard.Guards(typeof(T)))
            {
                return write(objs);
            }

            // The guard checks objects one at a time, so write them one at a time.
            bool success = true;
            foreach (T obj in objs.ToArray())
            {
                success &= Write(obj, () => write(new T[] { obj }));
            }
            return success;
        }

        private void Log<T>(T t) where T : BaseObject
        {
            Logger.RaceLog.LogStackTrace(this, t);
            try
            {
               Logger.RaceLog.Log(this, "Error writing " + typeof(T), t.ID, Logger.LogType.Error);
            }
            catch (Exception)
            {
                Logger.RaceLog.Log(this, "Error writing " + typeof(T), Logger.LogType.Error);
            }
        }

        private void Log<T>(IEnumerable<T> t) where T : BaseObject
        {
            Logger.RaceLog.LogStackTrace(this, t);
            try
            {
                Logger.RaceLog.Log(this, "Error writing " + typeof(T), string.Join(", ", t.Select(t => t.ID)), Logger.LogType.Error);
            }
            catch (Exception)
            {
                Logger.RaceLog.Log(this, "Error writing " + typeof(T) + "[]", Logger.LogType.Error);
            }
        }

        public bool Delete<T>(T t) where T : BaseObject, new()
        {
            if (Write(t, () => database.Delete(t)))
            {
                return true;
            }
            else
            {
                Log(t);
                return false;
            }
        }

        public bool Delete<T>(IEnumerable<T> t) where T : BaseObject, new()
        {
            if (WriteAll(t, objs => database.Delete(objs)))
            {
                return true;
            }
            else
            {
                Log(t);
                return false;
            }
        }


        public bool Insert<T>(T t) where T : BaseObject, new()
        {
            if (Write(t, () => database.Insert(t)))
            {
                return true;
            }
            else
            {
                Log(t);
                return false;
            }
        }

        public bool Insert<T>(IEnumerable<T> t) where T : BaseObject, new()
        {
            if (WriteAll(t, objs => database.Insert(objs)))
            {
                return true;
            }
            else
            {
                Log(t);
                return false;
            }
        }

        public bool Update<T>(T t) where T : BaseObject, new()
        {
            if (Write(t, () => database.Update(t)))
            {
                return true;
            }
            else
            {
                Log(t);
                return false;
            }
        }

        public bool Update<T>(IEnumerable<T> t) where T : BaseObject, new()
        {
            if (WriteAll(t, objs => database.Update(objs)))
            {
                return true;
            }
            else
            {
                Log(t);
                return false;
            }
        }

        public bool Upsert<T>(T t) where T : BaseObject, new()
        {
            if (Write(t, () => database.Upsert(t)))
            {
                return true;
            }
            else
            {
                Log(t);
                return false;
            }
        }

        public bool Upsert<T>(IEnumerable<T> t) where T : BaseObject, new()
        {
            if (WriteAll(t, objs => database.Upsert(objs)))
            {
                return true;
            }
            else
            {
                Log(t);
                return false;
            }
        }
    }
}
