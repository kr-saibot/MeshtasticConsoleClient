using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Meshtastic.Client
{
    public enum MessageDirection { Incoming, Outgoing }
    public enum StoredMessageKind { Channel, Direct }
    public enum StoredNodeSort { Name, NodeId, LastReceived }

    public sealed class StoredMeshNode
    {
        public uint NodeNumber { get; internal set; }
        public string NodeId { get; internal set; }
        public string LongName { get; internal set; }
        public string ShortName { get; internal set; }
        public DateTime LastReceivedUtc { get; internal set; }
        public bool IsFavorite { get; internal set; }
        public double? Latitude { get; internal set; }
        public double? Longitude { get; internal set; }
        public uint? BatteryLevel { get; internal set; }
        public uint? HopsAway { get; internal set; }
    }

    public sealed class StoredTelemetry
    {
        public Guid Id { get; internal set; }
        public uint NodeNumber { get; internal set; }
        public DateTime ReceivedAtUtc { get; internal set; }
        public DateTime? TelemetryTimeUtc { get; internal set; }
        public string Type { get; internal set; }
        public uint? BatteryLevel { get; internal set; }
        public double? Voltage { get; internal set; }
        public double? ChannelUtilization { get; internal set; }
        public double? AirUtilTx { get; internal set; }
        public uint? UptimeSeconds { get; internal set; }
        public double? Temperature { get; internal set; }
        public double? RelativeHumidity { get; internal set; }
        public double? BarometricPressure { get; internal set; }
        public double? Latitude { get; internal set; }
        public double? Longitude { get; internal set; }
        public int? Altitude { get; internal set; }
        public string RawTelemetry { get; internal set; }
    }

    public sealed class DirectMessageStatistics
    {
        public uint NodeNumber { get; internal set; }
        public int SentCount { get; internal set; }
        public int ReceivedCount { get; internal set; }
        public int NewCount { get; internal set; }
    }
    public sealed class ChannelMessageStatistics
    {
        public int? ChannelIndex { get; internal set; }
        public string ChannelName { get; internal set; }
        public int SentCount { get; internal set; }
        public int ReceivedCount { get; internal set; }
        public int NewCount { get; internal set; }
    }

    public sealed class StoredMeshMessage
    {
        public Guid Id { get; internal set; }
        public DateTime OccurredUtc { get; internal set; }
        public DateTime CreatedUtc { get; internal set; }
        public MessageDirection Direction { get; internal set; }
        public StoredMessageKind Kind { get; internal set; }
        public uint? PacketId { get; internal set; }
        public uint? FromNode { get; internal set; }
        public uint? ToNode { get; internal set; }
        public int? ChannelIndex { get; internal set; }
        public string ChannelName { get; internal set; }
        public string Text { get; internal set; }
        public string DeliveryStatus { get; internal set; }
        public string DeliveryError { get; internal set; }
        public bool IsNew { get; internal set; }
        public double? Latitude { get; internal set; }
        public double? Longitude { get; internal set; }
        public string RawMetadata { get; internal set; }
    }

    /// <summary>Persists Meshtastic text messages through SQLite or any ADO.NET provider (for example SQL Server).</summary>
    public sealed class MeshtasticMessageStore : IDisposable
    {
        private readonly object _sync = new object();
        private readonly DbProviderFactory _factory;
        private readonly string _connectionString;
        private readonly bool _sqlite;

        public MeshtasticMessageStore(DbProviderFactory factory, string connectionString, bool isSqlite)
        {
            _factory = factory ?? throw new ArgumentNullException("factory");
            _connectionString = connectionString ?? throw new ArgumentNullException("connectionString");
            _sqlite = isSqlite;
        }
        public static MeshtasticMessageStore CreateSqlite(string databaseFile)
        {
            SqliteProviderInitializer.Initialize();
            return new MeshtasticMessageStore(SqliteFactory.Instance, "Data Source=" + databaseFile + ";Cache=Shared;Pooling=True;Default Timeout=5", true);
        }
        public static MeshtasticMessageStore CreateSqlServer(string connectionString)
        {
            return new MeshtasticMessageStore(DbProviderFactories.GetFactory("System.Data.SqlClient"), connectionString, false);
        }

        public void Initialize()
        {
            // WAL permits readers (the UI) and writers (incoming packets) to run concurrently.
            // It is persistent per database file and is safe to execute for existing databases.
            if (_sqlite) Execute("PRAGMA journal_mode=WAL", null);
            var sql = _sqlite
                ? "CREATE TABLE IF NOT EXISTS MeshtasticMessages (Id TEXT PRIMARY KEY, OccurredUtc TEXT NOT NULL, CreatedUtc TEXT NOT NULL, Direction TEXT NOT NULL, Kind TEXT NOT NULL, PacketId INTEGER NULL, FromNode INTEGER NULL, ToNode INTEGER NULL, ChannelIndex INTEGER NULL, ChannelName TEXT NULL, Text TEXT NULL, DeliveryStatus TEXT NULL, DeliveryError TEXT NULL, IsNew INTEGER NOT NULL DEFAULT 1, Latitude REAL NULL, Longitude REAL NULL, RawMetadata TEXT NULL)"
                : "IF OBJECT_ID('MeshtasticMessages','U') IS NULL CREATE TABLE MeshtasticMessages (Id UNIQUEIDENTIFIER PRIMARY KEY, OccurredUtc DATETIME2 NOT NULL, CreatedUtc DATETIME2 NOT NULL, Direction NVARCHAR(16) NOT NULL, Kind NVARCHAR(16) NOT NULL, PacketId BIGINT NULL, FromNode BIGINT NULL, ToNode BIGINT NULL, ChannelIndex INT NULL, ChannelName NVARCHAR(256) NULL, Text NVARCHAR(MAX) NULL, DeliveryStatus NVARCHAR(32) NULL, DeliveryError NVARCHAR(128) NULL, IsNew BIT NOT NULL DEFAULT 1, Latitude FLOAT NULL, Longitude FLOAT NULL, RawMetadata NVARCHAR(MAX) NULL)";
            Execute(sql, null);
            // Migration for databases created by an earlier version of this class.
            Execute(_sqlite ? "ALTER TABLE MeshtasticMessages ADD COLUMN IsNew INTEGER NOT NULL DEFAULT 1" : "ALTER TABLE MeshtasticMessages ADD IsNew BIT NOT NULL DEFAULT 1", null, ignoreFailure: true);
            // Older database files received IsNew=1 as the column default. Sent messages are
            // never unread, including existing rows after the schema migration.
            Execute("UPDATE MeshtasticMessages SET IsNew=0 WHERE Direction='Outgoing'", null);
            Execute("CREATE INDEX " + (_sqlite ? "IF NOT EXISTS " : "") + "IX_MeshtasticMessages_Time ON MeshtasticMessages(OccurredUtc)", null, ignoreFailure: !_sqlite);
            Execute("CREATE INDEX " + (_sqlite ? "IF NOT EXISTS " : "") + "IX_MeshtasticMessages_ChannelTime ON MeshtasticMessages(Kind,ChannelIndex,OccurredUtc)", null, ignoreFailure: !_sqlite);
            Execute("CREATE INDEX " + (_sqlite ? "IF NOT EXISTS " : "") + "IX_MeshtasticMessages_FromTime ON MeshtasticMessages(Kind,FromNode,OccurredUtc)", null, ignoreFailure: !_sqlite);
            Execute("CREATE INDEX " + (_sqlite ? "IF NOT EXISTS " : "") + "IX_MeshtasticMessages_ToTime ON MeshtasticMessages(Kind,ToNode,OccurredUtc)", null, ignoreFailure: !_sqlite);
            var nodeSql = _sqlite
                ? "CREATE TABLE IF NOT EXISTS MeshtasticNodes (NodeNumber INTEGER PRIMARY KEY, NodeId TEXT NULL, LongName TEXT NULL, ShortName TEXT NULL, LastReceivedUtc TEXT NOT NULL, IsFavorite INTEGER NOT NULL DEFAULT 0, Latitude REAL NULL, Longitude REAL NULL, BatteryLevel INTEGER NULL, HopsAway INTEGER NULL)"
                : "IF OBJECT_ID('MeshtasticNodes','U') IS NULL CREATE TABLE MeshtasticNodes (NodeNumber BIGINT PRIMARY KEY, NodeId NVARCHAR(32) NULL, LongName NVARCHAR(256) NULL, ShortName NVARCHAR(32) NULL, LastReceivedUtc DATETIME2 NOT NULL, IsFavorite BIT NOT NULL DEFAULT 0, Latitude FLOAT NULL, Longitude FLOAT NULL, BatteryLevel INT NULL, HopsAway INT NULL)";
            Execute(nodeSql, null);
            Execute(_sqlite ? "ALTER TABLE MeshtasticNodes ADD COLUMN HopsAway INTEGER NULL" : "ALTER TABLE MeshtasticNodes ADD HopsAway INT NULL", null, ignoreFailure: true);
            Execute("CREATE INDEX " + (_sqlite ? "IF NOT EXISTS " : "") + "IX_MeshtasticNodes_LastReceived ON MeshtasticNodes(LastReceivedUtc)", null, ignoreFailure: !_sqlite);
            var telemetrySql = _sqlite
                ? "CREATE TABLE IF NOT EXISTS MeshtasticTelemetry (Id TEXT PRIMARY KEY, NodeNumber INTEGER NOT NULL, ReceivedAtUtc TEXT NOT NULL, TelemetryTimeUtc TEXT NULL, Type TEXT NOT NULL, BatteryLevel INTEGER NULL, Voltage REAL NULL, ChannelUtilization REAL NULL, AirUtilTx REAL NULL, UptimeSeconds INTEGER NULL, Temperature REAL NULL, RelativeHumidity REAL NULL, BarometricPressure REAL NULL, Latitude REAL NULL, Longitude REAL NULL, Altitude INTEGER NULL, RawTelemetry TEXT NOT NULL)"
                : "IF OBJECT_ID('MeshtasticTelemetry','U') IS NULL CREATE TABLE MeshtasticTelemetry (Id UNIQUEIDENTIFIER PRIMARY KEY, NodeNumber BIGINT NOT NULL, ReceivedAtUtc DATETIME2 NOT NULL, TelemetryTimeUtc DATETIME2 NULL, Type NVARCHAR(64) NOT NULL, BatteryLevel INT NULL, Voltage FLOAT NULL, ChannelUtilization FLOAT NULL, AirUtilTx FLOAT NULL, UptimeSeconds BIGINT NULL, Temperature FLOAT NULL, RelativeHumidity FLOAT NULL, BarometricPressure FLOAT NULL, Latitude FLOAT NULL, Longitude FLOAT NULL, Altitude INT NULL, RawTelemetry NVARCHAR(MAX) NOT NULL)";
            Execute(telemetrySql, null);
            Execute(_sqlite ? "ALTER TABLE MeshtasticTelemetry ADD COLUMN Latitude REAL NULL" : "ALTER TABLE MeshtasticTelemetry ADD Latitude FLOAT NULL", null, ignoreFailure: true);
            Execute(_sqlite ? "ALTER TABLE MeshtasticTelemetry ADD COLUMN Longitude REAL NULL" : "ALTER TABLE MeshtasticTelemetry ADD Longitude FLOAT NULL", null, ignoreFailure: true);
            Execute(_sqlite ? "ALTER TABLE MeshtasticTelemetry ADD COLUMN Altitude INTEGER NULL" : "ALTER TABLE MeshtasticTelemetry ADD Altitude INT NULL", null, ignoreFailure: true);
            Execute("CREATE INDEX " + (_sqlite ? "IF NOT EXISTS " : "") + "IX_MeshtasticTelemetry_NodeTime ON MeshtasticTelemetry(NodeNumber,ReceivedAtUtc)", null, ignoreFailure: !_sqlite);
            Execute("CREATE INDEX " + (_sqlite ? "IF NOT EXISTS " : "") + "IX_MeshtasticTelemetry_PositionNodeTime ON MeshtasticTelemetry(NodeNumber,ReceivedAtUtc) WHERE Latitude IS NOT NULL AND Longitude IS NOT NULL", null, ignoreFailure: !_sqlite);
        }

        public Guid AddIncoming(MeshMessage message, double? latitude = null, double? longitude = null, bool isNew = true)
        {
            if (message == null) throw new ArgumentNullException("message");
            return Insert(new StoredMeshMessage
            {
                Id = Guid.NewGuid(), OccurredUtc = message.ReceivedAtUtc, CreatedUtc = DateTime.UtcNow,
                Direction = MessageDirection.Incoming, Kind = message.IsChannelMessage ? StoredMessageKind.Channel : StoredMessageKind.Direct,
                PacketId = message.Packet.Id == 0 ? (uint?)null : message.Packet.Id, FromNode = message.From, ToNode = message.To,
                ChannelIndex = message.ChannelIndex.HasValue ? (int?)message.ChannelIndex.Value : null, Text = message.Text,
                IsNew = isNew, Latitude = latitude, Longitude = longitude, RawMetadata = message.Packet.ToString()
            });
        }

        public Guid AddOutgoing(uint packetId, uint fromNode, uint toNode, string text, int? channelIndex, string channelName = null, double? latitude = null, double? longitude = null)
        {
            return Insert(new StoredMeshMessage
            {
                Id = Guid.NewGuid(), OccurredUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow,
                Direction = MessageDirection.Outgoing, Kind = toNode == UInt32.MaxValue ? StoredMessageKind.Channel : StoredMessageKind.Direct,
                PacketId = packetId, FromNode = fromNode, ToNode = toNode, ChannelIndex = channelIndex, ChannelName = channelName,
                Text = text, DeliveryStatus = "Submitted", IsNew = false, Latitude = latitude, Longitude = longitude
            });
        }

        public void UpdateDeliveryStatus(uint packetId, MessageDeliveryState state, string error = null)
        {
            Execute("UPDATE MeshtasticMessages SET DeliveryStatus=@status, DeliveryError=@error WHERE PacketId=@packetId", delegate(DbCommand c) { Add(c, "@status", state.ToString()); Add(c, "@error", error); Add(c, "@packetId", (long)packetId); });
        }
        public void Delete(Guid id) { Execute("DELETE FROM MeshtasticMessages WHERE Id=@id", delegate(DbCommand c) { Add(c, "@id", IdValue(id)); }); }
        public IList<StoredMeshMessage> GetDirectMessages(uint node) { return Query("SELECT * FROM MeshtasticMessages WHERE Kind='Direct' AND (FromNode=@node OR ToNode=@node) ORDER BY OccurredUtc", delegate(DbCommand c) { Add(c, "@node", (long)node); }); }
        public void DeleteDirectMessages(uint node) { Execute("DELETE FROM MeshtasticMessages WHERE Kind='Direct' AND (FromNode=@node OR ToNode=@node)", delegate(DbCommand c) { Add(c, "@node", (long)node); }); }
        public IList<StoredMeshMessage> GetChannelMessages() { return Query("SELECT * FROM MeshtasticMessages WHERE Kind='Channel' ORDER BY OccurredUtc", null); }
        public IList<StoredMeshMessage> GetChannelMessages(int channelIndex) { return Query("SELECT * FROM MeshtasticMessages WHERE Kind='Channel' AND ChannelIndex=@channel ORDER BY OccurredUtc", delegate(DbCommand c) { Add(c, "@channel", channelIndex); }); }
        public void DeleteChannelMessages() { Execute("DELETE FROM MeshtasticMessages WHERE Kind='Channel'", null); }
        public void DeleteChannelMessages(int channelIndex) { Execute("DELETE FROM MeshtasticMessages WHERE Kind='Channel' AND ChannelIndex=@channel", delegate(DbCommand c) { Add(c, "@channel", channelIndex); }); }
        public int CountNewChannelMessages(int channelIndex) { return Count("SELECT COUNT(*) FROM MeshtasticMessages WHERE Kind='Channel' AND ChannelIndex=@channel AND IsNew=1", delegate(DbCommand c) { Add(c, "@channel", channelIndex); }); }
        public int CountNewDirectMessages(uint node) { return Count("SELECT COUNT(*) FROM MeshtasticMessages WHERE Kind='Direct' AND (FromNode=@node OR ToNode=@node) AND IsNew=1", delegate(DbCommand c) { Add(c, "@node", (long)node); }); }
        public int CountNewMessages() { return Count("SELECT COUNT(*) FROM MeshtasticMessages WHERE IsNew=1", null); }
        public void MarkChannelMessagesRead(int channelIndex) { Execute("UPDATE MeshtasticMessages SET IsNew=0 WHERE Kind='Channel' AND ChannelIndex=@channel", delegate(DbCommand c) { Add(c, "@channel", channelIndex); }); }
        public void MarkDirectMessagesRead(uint node) { Execute("UPDATE MeshtasticMessages SET IsNew=0 WHERE Kind='Direct' AND (FromNode=@node OR ToNode=@node)", delegate(DbCommand c) { Add(c, "@node", (long)node); }); }
        public void MarkAllMessagesRead() { Execute("UPDATE MeshtasticMessages SET IsNew=0", null); }
        public void DeleteAllMessages() { Execute("DELETE FROM MeshtasticMessages", null); }
        public IList<DirectMessageStatistics> GetDirectMessageStatistics()
        {
            const string sql = "SELECT CASE WHEN Direction='Outgoing' THEN ToNode ELSE FromNode END AS NodeNumber, SUM(CASE WHEN Direction='Outgoing' THEN 1 ELSE 0 END) AS SentCount, SUM(CASE WHEN Direction='Incoming' THEN 1 ELSE 0 END) AS ReceivedCount, SUM(CASE WHEN Direction='Incoming' AND IsNew=1 THEN 1 ELSE 0 END) AS NewCount FROM MeshtasticMessages WHERE Kind='Direct' GROUP BY CASE WHEN Direction='Outgoing' THEN ToNode ELSE FromNode END ORDER BY NodeNumber";
            return QueryDirectStatistics(sql);
        }
        public IList<ChannelMessageStatistics> GetChannelMessageStatistics()
        {
            const string sql = "SELECT ChannelIndex, MAX(ChannelName) AS ChannelName, SUM(CASE WHEN Direction='Outgoing' THEN 1 ELSE 0 END) AS SentCount, SUM(CASE WHEN Direction='Incoming' THEN 1 ELSE 0 END) AS ReceivedCount, SUM(CASE WHEN Direction='Incoming' AND IsNew=1 THEN 1 ELSE 0 END) AS NewCount FROM MeshtasticMessages WHERE Kind='Channel' GROUP BY ChannelIndex ORDER BY ChannelIndex";
            return QueryChannelStatistics(sql);
        }

        /// <summary>Inserts a node or updates its latest device-reported data. The favorite flag is preserved.</summary>
        public void AddOrUpdateNode(MeshNode node)
        {
            if (node == null) throw new ArgumentNullException("node");
            var timestamp = node.LastHeardUtc ?? DateTime.UtcNow;
            var hasLastHeard = node.LastHeardUtc.HasValue;
            lock (_sync)
            {
                if (_sqlite)
                {
                    const string upsert = "INSERT INTO MeshtasticNodes (NodeNumber,NodeId,LongName,ShortName,LastReceivedUtc,IsFavorite,Latitude,Longitude,BatteryLevel,HopsAway) VALUES (@number,@id,@longName,@shortName,@lastReceived,0,@lat,@lon,@battery,@hops) ON CONFLICT(NodeNumber) DO UPDATE SET NodeId=excluded.NodeId,LongName=excluded.LongName,ShortName=excluded.ShortName,LastReceivedUtc=CASE WHEN @hasLastHeard=1 THEN excluded.LastReceivedUtc ELSE MeshtasticNodes.LastReceivedUtc END,Latitude=excluded.Latitude,Longitude=excluded.Longitude,BatteryLevel=excluded.BatteryLevel,HopsAway=excluded.HopsAway";
                    Execute(upsert, delegate(DbCommand c) { AddNodeParameters(c, node, timestamp, hasLastHeard); });
                    return;
                }
                var update = "UPDATE MeshtasticNodes SET NodeId=@id, LongName=@longName, ShortName=@shortName, LastReceivedUtc=CASE WHEN @hasLastHeard=1 THEN @lastReceived ELSE LastReceivedUtc END, Latitude=@lat, Longitude=@lon, BatteryLevel=@battery, HopsAway=@hops WHERE NodeNumber=@number";
                var affected = ExecuteNonQuery(update, delegate(DbCommand c) { AddNodeParameters(c, node, timestamp, hasLastHeard); });
                if (affected == 0)
                {
                    const string insert = "INSERT INTO MeshtasticNodes (NodeNumber,NodeId,LongName,ShortName,LastReceivedUtc,IsFavorite,Latitude,Longitude,BatteryLevel,HopsAway) VALUES (@number,@id,@longName,@shortName,@lastReceived,@favorite,@lat,@lon,@battery,@hops)";
                    Execute(insert, delegate(DbCommand c) { AddNodeParameters(c, node, timestamp, hasLastHeard); Add(c, "@favorite", _sqlite ? (object)0 : false); });
                }
            }
        }
        public IList<StoredMeshNode> GetNodes(StoredNodeSort sort = StoredNodeSort.Name)
        {
            var orderBy = sort == StoredNodeSort.NodeId ? "NodeId, NodeNumber" : sort == StoredNodeSort.LastReceived ? "LastReceivedUtc DESC, LongName" : "LongName, NodeId";
            return QueryNodes("SELECT * FROM MeshtasticNodes ORDER BY " + orderBy);
        }
        public void SetNodeFavorite(uint nodeNumber, bool isFavorite)
        {
            Execute("UPDATE MeshtasticNodes SET IsFavorite=@favorite WHERE NodeNumber=@number", delegate(DbCommand c) { Add(c, "@favorite", _sqlite ? (object)(isFavorite ? 1 : 0) : isFavorite); Add(c, "@number", (long)nodeNumber); });
        }
        /// <summary>Adds a manually created node or updates its name and ID. Manually created nodes are always favorites.</summary>
        public void AddOrUpdateManualNode(uint nodeNumber, string nodeId, string longName)
        {
            if (String.IsNullOrWhiteSpace(nodeId)) throw new ArgumentException("A Meshtastic ID is required.", "nodeId");
            if (String.IsNullOrWhiteSpace(longName)) throw new ArgumentException("A node name is required.", "longName");
            var timestamp = DateTime.UtcNow;
            lock (_sync)
            {
                var update = "UPDATE MeshtasticNodes SET NodeId=@id, LongName=@longName, ShortName=@shortName, LastReceivedUtc=@lastReceived, IsFavorite=@favorite WHERE NodeNumber=@number";
                var affected = ExecuteNonQuery(update, delegate(DbCommand c)
                {
                    Add(c, "@number", (long)nodeNumber); Add(c, "@id", nodeId); Add(c, "@longName", longName); Add(c, "@shortName", longName.Length > 4 ? longName.Substring(0, 4) : longName); Add(c, "@lastReceived", TimeValue(timestamp)); Add(c, "@favorite", _sqlite ? (object)1 : true);
                });
                if (affected == 0)
                {
                    const string insert = "INSERT INTO MeshtasticNodes (NodeNumber,NodeId,LongName,ShortName,LastReceivedUtc,IsFavorite,Latitude,Longitude,BatteryLevel,HopsAway) VALUES (@number,@id,@longName,@shortName,@lastReceived,@favorite,NULL,NULL,NULL,NULL)";
                    Execute(insert, delegate(DbCommand c)
                    {
                        Add(c, "@number", (long)nodeNumber); Add(c, "@id", nodeId); Add(c, "@longName", longName); Add(c, "@shortName", longName.Length > 4 ? longName.Substring(0, 4) : longName); Add(c, "@lastReceived", TimeValue(timestamp)); Add(c, "@favorite", _sqlite ? (object)1 : true);
                    });
                }
            }
        }
        public void DeleteNode(uint nodeNumber) { Execute("DELETE FROM MeshtasticNodes WHERE NodeNumber=@number", delegate(DbCommand c) { Add(c, "@number", (long)nodeNumber); }); }
        public void DeleteAllNodes() { Execute("DELETE FROM MeshtasticNodes", null); }

        public Guid AddTelemetry(MeshTelemetry telemetry)
        {
            if (telemetry == null) throw new ArgumentNullException("telemetry");
            var device = telemetry.Telemetry.DeviceMetrics;
            var environment = telemetry.Telemetry.EnvironmentMetrics;
            var id = Guid.NewGuid();
            const string sql = "INSERT INTO MeshtasticTelemetry (Id,NodeNumber,ReceivedAtUtc,TelemetryTimeUtc,Type,BatteryLevel,Voltage,ChannelUtilization,AirUtilTx,UptimeSeconds,Temperature,RelativeHumidity,BarometricPressure,RawTelemetry) VALUES (@id,@node,@received,@telemetryTime,@type,@battery,@voltage,@channelUtilization,@airUtilTx,@uptime,@temperature,@humidity,@pressure,@raw)";
            Execute(sql, delegate(DbCommand c)
            {
                Add(c, "@id", IdValue(id)); Add(c, "@node", (long)telemetry.From); Add(c, "@received", TimeValue(telemetry.ReceivedAtUtc)); Add(c, "@telemetryTime", telemetry.TelemetryTimeUtc.HasValue ? TimeValue(telemetry.TelemetryTimeUtc.Value) : null); Add(c, "@type", telemetry.Type);
                Add(c, "@battery", device != null && device.HasBatteryLevel ? (object)(long)device.BatteryLevel : null); Add(c, "@voltage", device != null && device.HasVoltage ? (object)device.Voltage : (environment != null && environment.HasVoltage ? (object)environment.Voltage : null)); Add(c, "@channelUtilization", device != null && device.HasChannelUtilization ? (object)device.ChannelUtilization : null); Add(c, "@airUtilTx", device != null && device.HasAirUtilTx ? (object)device.AirUtilTx : null); Add(c, "@uptime", device != null && device.HasUptimeSeconds ? (object)(long)device.UptimeSeconds : null);
                Add(c, "@temperature", environment != null && environment.HasTemperature ? (object)environment.Temperature : null); Add(c, "@humidity", environment != null && environment.HasRelativeHumidity ? (object)environment.RelativeHumidity : null); Add(c, "@pressure", environment != null && environment.HasBarometricPressure ? (object)environment.BarometricPressure : null); Add(c, "@raw", telemetry.Telemetry.ToString());
            });
            return id;
        }
        public Guid AddPosition(uint nodeNumber, Meshtastic.Protobufs.Position position, DateTime receivedAtUtc)
        {
            if (position == null) throw new ArgumentNullException("position");
            var id = Guid.NewGuid();
            DateTime? positionTimeUtc = position.Timestamp != 0 ? UnixTime(position.Timestamp) : (position.Time != 0 ? UnixTime(position.Time) : (DateTime?)null);
            const string sql = "INSERT INTO MeshtasticTelemetry (Id,NodeNumber,ReceivedAtUtc,TelemetryTimeUtc,Type,Latitude,Longitude,Altitude,RawTelemetry) VALUES (@id,@node,@received,@positionTime,@type,@lat,@lon,@altitude,@raw)";
            Execute(sql, delegate(DbCommand c)
            {
                Add(c, "@id", IdValue(id)); Add(c, "@node", (long)nodeNumber); Add(c, "@received", TimeValue(receivedAtUtc)); Add(c, "@positionTime", positionTimeUtc.HasValue ? TimeValue(positionTimeUtc.Value) : null); Add(c, "@type", "Position");
                Add(c, "@lat", position.HasLatitudeI ? (object)(position.LatitudeI / 10000000d) : null); Add(c, "@lon", position.HasLongitudeI ? (object)(position.LongitudeI / 10000000d) : null); Add(c, "@altitude", position.HasAltitude ? (object)position.Altitude : null); Add(c, "@raw", position.ToString());
            });
            return id;
        }
        public IList<StoredTelemetry> GetTelemetry(uint nodeNumber, int maximumCount = 100)
        {
            if (maximumCount <= 0) throw new ArgumentOutOfRangeException("maximumCount");
            return QueryTelemetry("SELECT * FROM MeshtasticTelemetry WHERE NodeNumber=@node ORDER BY ReceivedAtUtc DESC", delegate(DbCommand c) { Add(c, "@node", (long)nodeNumber); }, maximumCount);
        }
        public IList<StoredTelemetry> GetTelemetry(int maximumCount = 100)
        {
            if (maximumCount <= 0) throw new ArgumentOutOfRangeException("maximumCount");
            return QueryTelemetry("SELECT * FROM MeshtasticTelemetry ORDER BY ReceivedAtUtc DESC", null, maximumCount);
        }
        public IList<StoredTelemetry> GetPositions(uint nodeNumber, int maximumCount = 5000)
        {
            if (maximumCount <= 0) throw new ArgumentOutOfRangeException("maximumCount");
            return QueryTelemetry("SELECT * FROM MeshtasticTelemetry WHERE NodeNumber=@node AND Latitude IS NOT NULL AND Longitude IS NOT NULL ORDER BY ReceivedAtUtc DESC", delegate(DbCommand c) { Add(c, "@node", (long)nodeNumber); }, maximumCount);
        }
        public IList<StoredTelemetry> GetPositions(uint nodeNumber, DateTime oldestUtc, DateTime newestUtc, int maximumCount = 5000)
        {
            if (maximumCount <= 0) throw new ArgumentOutOfRangeException("maximumCount");
            if (newestUtc < oldestUtc) throw new ArgumentException("The newest position time must not be earlier than the oldest position time.", "newestUtc");
            return QueryTelemetry("SELECT * FROM MeshtasticTelemetry WHERE NodeNumber=@node AND Latitude IS NOT NULL AND Longitude IS NOT NULL AND ReceivedAtUtc>=@oldest AND ReceivedAtUtc<=@newest ORDER BY ReceivedAtUtc DESC", delegate(DbCommand c) { Add(c, "@node", (long)nodeNumber); Add(c, "@oldest", TimeValue(oldestUtc)); Add(c, "@newest", TimeValue(newestUtc)); }, maximumCount);
        }        public IList<uint> GetPositionNodeNumbers()
        {
            lock (_sync) using (var connection = Open()) using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT DISTINCT NodeNumber FROM MeshtasticTelemetry WHERE Latitude IS NOT NULL AND Longitude IS NOT NULL ORDER BY NodeNumber";
                using (var reader = command.ExecuteReader())
                {
                    var result = new List<uint>();
                    while (reader.Read()) result.Add(Convert.ToUInt32(reader["NodeNumber"]));
                    return result;
                }
            }
        }
        public void DeleteTelemetry(uint nodeNumber)
        {
            Execute("DELETE FROM MeshtasticTelemetry WHERE NodeNumber=@node", delegate(DbCommand c) { Add(c, "@node", (long)nodeNumber); });
        }
        public void DeleteAllTelemetry() { Execute("DELETE FROM MeshtasticTelemetry", null); }

        private Guid Insert(StoredMeshMessage item)
        {
            const string sql = "INSERT INTO MeshtasticMessages (Id,OccurredUtc,CreatedUtc,Direction,Kind,PacketId,FromNode,ToNode,ChannelIndex,ChannelName,Text,DeliveryStatus,DeliveryError,IsNew,Latitude,Longitude,RawMetadata) VALUES (@id,@occurred,@created,@direction,@kind,@packetId,@from,@to,@channelIndex,@channelName,@text,@status,@error,@isNew,@lat,@lon,@raw)";
            Execute(sql, delegate(DbCommand c)
            {
                Add(c,"@id",IdValue(item.Id)); Add(c,"@occurred",TimeValue(item.OccurredUtc)); Add(c,"@created",TimeValue(item.CreatedUtc)); Add(c,"@direction",item.Direction.ToString()); Add(c,"@kind",item.Kind.ToString()); Add(c,"@packetId",NullableNumber(item.PacketId)); Add(c,"@from",NullableNumber(item.FromNode)); Add(c,"@to",NullableNumber(item.ToNode)); Add(c,"@channelIndex",item.ChannelIndex); Add(c,"@channelName",item.ChannelName); Add(c,"@text",item.Text); Add(c,"@status",item.DeliveryStatus); Add(c,"@error",item.DeliveryError); Add(c,"@isNew",_sqlite ? (object)(item.IsNew ? 1 : 0) : item.IsNew); Add(c,"@lat",item.Latitude); Add(c,"@lon",item.Longitude); Add(c,"@raw",item.RawMetadata);
            });
            return item.Id;
        }
        private IList<StoredMeshMessage> Query(string sql, Action<DbCommand> parameters)
        {
            lock (_sync)
            using (var connection = Open()) using (var command = connection.CreateCommand())
            {
                command.CommandText = sql; if (parameters != null) parameters(command);
                using (var reader = command.ExecuteReader())
                {
                    var list = new List<StoredMeshMessage>(); while (reader.Read()) list.Add(Read(reader)); return list;
                }
            }
        }
        private void Execute(string sql, Action<DbCommand> parameters, bool ignoreFailure = false)
        {
            try { lock (_sync) using (var connection = Open()) using (var command = connection.CreateCommand()) { command.CommandText = sql; if (parameters != null) parameters(command); command.ExecuteNonQuery(); } }
            catch when (ignoreFailure) { }
        }
        private int Count(string sql, Action<DbCommand> parameters)
        {
            lock (_sync) using (var connection = Open()) using (var command = connection.CreateCommand()) { command.CommandText = sql; if (parameters != null) parameters(command); return Convert.ToInt32(command.ExecuteScalar()); }
        }
        private int ExecuteNonQuery(string sql, Action<DbCommand> parameters)
        {
            lock (_sync) using (var connection = Open()) using (var command = connection.CreateCommand()) { command.CommandText = sql; if (parameters != null) parameters(command); return command.ExecuteNonQuery(); }
        }
        private IList<DirectMessageStatistics> QueryDirectStatistics(string sql)
        {
            lock (_sync) using (var connection = Open()) using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                using (var reader = command.ExecuteReader())
                {
                    var result = new List<DirectMessageStatistics>();
                    while (reader.Read()) result.Add(new DirectMessageStatistics { NodeNumber = Convert.ToUInt32(reader["NodeNumber"]), SentCount = Convert.ToInt32(reader["SentCount"]), ReceivedCount = Convert.ToInt32(reader["ReceivedCount"]), NewCount = Convert.ToInt32(reader["NewCount"]) });
                    return result;
                }
            }
        }
        private IList<ChannelMessageStatistics> QueryChannelStatistics(string sql)
        {
            lock (_sync) using (var connection = Open()) using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                using (var reader = command.ExecuteReader())
                {
                    var result = new List<ChannelMessageStatistics>();
                    while (reader.Read()) result.Add(new ChannelMessageStatistics { ChannelIndex = reader["ChannelIndex"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["ChannelIndex"]), ChannelName = reader["ChannelName"] == DBNull.Value ? null : Convert.ToString(reader["ChannelName"]), SentCount = Convert.ToInt32(reader["SentCount"]), ReceivedCount = Convert.ToInt32(reader["ReceivedCount"]), NewCount = Convert.ToInt32(reader["NewCount"]) });
                    return result;
                }
            }
        }
        private IList<StoredMeshNode> QueryNodes(string sql)
        {
            lock (_sync) using (var connection = Open()) using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                using (var reader = command.ExecuteReader())
                {
                    var result = new List<StoredMeshNode>();
                    while (reader.Read()) result.Add(new StoredMeshNode { NodeNumber = Convert.ToUInt32(reader["NodeNumber"]), NodeId = Convert.ToString(reader["NodeId"]), LongName = Convert.ToString(reader["LongName"]), ShortName = Convert.ToString(reader["ShortName"]), LastReceivedUtc = ReadTime(reader["LastReceivedUtc"]), IsFavorite = Convert.ToInt32(reader["IsFavorite"]) != 0, Latitude = reader["Latitude"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["Latitude"]), Longitude = reader["Longitude"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["Longitude"]), BatteryLevel = reader["BatteryLevel"] == DBNull.Value ? (uint?)null : Convert.ToUInt32(reader["BatteryLevel"]), HopsAway = reader["HopsAway"] == DBNull.Value ? (uint?)null : Convert.ToUInt32(reader["HopsAway"]) });
                    return result;
                }
            }
        }
        private IList<StoredTelemetry> QueryTelemetry(string sql, Action<DbCommand> parameters, int maximumCount)
        {
            lock (_sync) using (var connection = Open()) using (var command = connection.CreateCommand())
            {
                command.CommandText = sql; if (parameters != null) parameters(command);
                using (var reader = command.ExecuteReader())
                {
                    var result = new List<StoredTelemetry>();
                    while (reader.Read() && result.Count < maximumCount)
                    {
                        result.Add(new StoredTelemetry
                        {
                            Id = Guid.Parse(Convert.ToString(reader["Id"])), NodeNumber = Convert.ToUInt32(reader["NodeNumber"]), ReceivedAtUtc = ReadTime(reader["ReceivedAtUtc"]), TelemetryTimeUtc = reader["TelemetryTimeUtc"] == DBNull.Value ? (DateTime?)null : ReadTime(reader["TelemetryTimeUtc"]), Type = Convert.ToString(reader["Type"]), BatteryLevel = reader["BatteryLevel"] == DBNull.Value ? (uint?)null : Convert.ToUInt32(reader["BatteryLevel"]), Voltage = reader["Voltage"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["Voltage"]), ChannelUtilization = reader["ChannelUtilization"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["ChannelUtilization"]), AirUtilTx = reader["AirUtilTx"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["AirUtilTx"]), UptimeSeconds = reader["UptimeSeconds"] == DBNull.Value ? (uint?)null : Convert.ToUInt32(reader["UptimeSeconds"]), Temperature = reader["Temperature"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["Temperature"]), RelativeHumidity = reader["RelativeHumidity"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["RelativeHumidity"]), BarometricPressure = reader["BarometricPressure"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["BarometricPressure"]), Latitude = reader["Latitude"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["Latitude"]), Longitude = reader["Longitude"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["Longitude"]), Altitude = reader["Altitude"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["Altitude"]), RawTelemetry = Convert.ToString(reader["RawTelemetry"])
                        });
                    }
                    return result;
                }
            }
        }
        private DbConnection Open()
        {
            var connection = _factory.CreateConnection();
            connection.ConnectionString = _connectionString;
            connection.Open();
            if (_sqlite)
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY";
                    command.ExecuteNonQuery();
                }
            }
            return connection;
        }
        private object IdValue(Guid id) { return _sqlite ? (object)id.ToString("D") : id; }
        private object TimeValue(DateTime value) { return _sqlite ? (object)value.ToUniversalTime().ToString("o") : value; }
        private static object NullableNumber(uint? value) { return value.HasValue ? (object)(long)value.Value : DBNull.Value; }
        private void AddNodeParameters(DbCommand command, MeshNode node, DateTime timestamp, bool hasLastHeard)
        {
            Add(command, "@number", (long)node.Number); Add(command, "@id", node.Id); Add(command, "@longName", node.LongName); Add(command, "@shortName", node.ShortName); Add(command, "@lastReceived", TimeValue(timestamp)); Add(command, "@hasLastHeard", _sqlite ? (object)(hasLastHeard ? 1 : 0) : hasLastHeard); Add(command, "@lat", node.Latitude); Add(command, "@lon", node.Longitude); Add(command, "@battery", node.BatteryLevel.HasValue ? (object)(long)node.BatteryLevel.Value : DBNull.Value); Add(command, "@hops", node.HopsAway.HasValue ? (object)(long)node.HopsAway.Value : DBNull.Value);
        }
        private static void Add(DbCommand command, string name, object value) { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; command.Parameters.Add(p); }
        private StoredMeshMessage Read(IDataRecord r)
        {
            Func<string, object> v = delegate(string name) { var value = r[name]; return value == DBNull.Value ? null : value; };
            return new StoredMeshMessage { Id = Guid.Parse(Convert.ToString(v("Id"))), OccurredUtc = ReadTime(v("OccurredUtc")), CreatedUtc = ReadTime(v("CreatedUtc")), Direction = (MessageDirection)Enum.Parse(typeof(MessageDirection), Convert.ToString(v("Direction"))), Kind = (StoredMessageKind)Enum.Parse(typeof(StoredMessageKind), Convert.ToString(v("Kind"))), PacketId = ReadUInt(v("PacketId")), FromNode = ReadUInt(v("FromNode")), ToNode = ReadUInt(v("ToNode")), ChannelIndex = v("ChannelIndex") == null ? (int?)null : Convert.ToInt32(v("ChannelIndex")), ChannelName = Convert.ToString(v("ChannelName")), Text = Convert.ToString(v("Text")), DeliveryStatus = Convert.ToString(v("DeliveryStatus")), DeliveryError = Convert.ToString(v("DeliveryError")), IsNew = v("IsNew") != null && Convert.ToInt32(v("IsNew")) != 0, Latitude = v("Latitude") == null ? (double?)null : Convert.ToDouble(v("Latitude")), Longitude = v("Longitude") == null ? (double?)null : Convert.ToDouble(v("Longitude")), RawMetadata = Convert.ToString(v("RawMetadata")) };
        }
        private static DateTime UnixTime(uint seconds) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds); }
        private static uint? ReadUInt(object value) { return value == null ? (uint?)null : Convert.ToUInt32(value); }
        private static DateTime ReadTime(object value) { return value is DateTime ? ((DateTime)value).ToUniversalTime() : DateTime.Parse(Convert.ToString(value), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(); }
        public void Dispose() { }
    }

    /// <summary>Selects the SQLite provider chosen at build time.</summary>
    internal static class SqliteProviderInitializer
    {
        private static readonly object Sync = new object();
        private static bool _initialized;

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized) return;
#if SYSTEM_SQLITE
                raw.SetProvider(new SQLite3Provider_sqlite3());
#else
                Batteries_V2.Init();
#endif
                _initialized = true;
            }
        }
    }
}
