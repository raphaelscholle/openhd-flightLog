using Microsoft.Data.Sqlite;
using OpenHdFlightLog.Models;

namespace OpenHdFlightLog.Services;

public sealed class FlightLogDatabase
{
    private readonly Action<DebugEventRecord>? debug;

    public string DatabasePath { get; }

    public FlightLogDatabase(Action<DebugEventRecord>? debug = null)
    {
        this.debug = debug;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenHdFlightLog");
        Directory.CreateDirectory(appData);
        DatabasePath = Path.Combine(appData, "flightlogs.sqlite");
        EnsureSchema();
    }

    public void EnsureSchema()
    {
        using var connection = OpenConnection();
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS log_files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_name TEXT NOT NULL,
                path TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                message_count INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS message_types (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id INTEGER NOT NULL UNIQUE,
                name TEXT NOT NULL,
                dialect TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS mavlink_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                log_file_id INTEGER NOT NULL,
                message_type_id INTEGER NOT NULL,
                packet_index INTEGER NOT NULL,
                packet_time_ms INTEGER NOT NULL DEFAULT 0,
                packet_timestamp TEXT NOT NULL DEFAULT '',
                byte_offset INTEGER NOT NULL,
                mavlink_version INTEGER NOT NULL,
                sequence INTEGER NOT NULL,
                system_id INTEGER NOT NULL,
                component_id INTEGER NOT NULL,
                route TEXT NOT NULL DEFAULT '',
                payload_length INTEGER NOT NULL,
                checksum TEXT NOT NULL,
                raw_packet_hex TEXT NOT NULL,
                FOREIGN KEY (log_file_id) REFERENCES log_files(id) ON DELETE CASCADE,
                FOREIGN KEY (message_type_id) REFERENCES message_types(id)
            );

            CREATE TABLE IF NOT EXISTS message_fields (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id INTEGER NOT NULL,
                field_name TEXT NOT NULL,
                value_text TEXT NOT NULL,
                numeric_value REAL NULL,
                unit TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (message_id) REFERENCES mavlink_messages(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS user_variables (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                value_text TEXT NOT NULL,
                data_type TEXT NOT NULL DEFAULT 'text',
                notes TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS message_definitions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id INTEGER NOT NULL UNIQUE,
                name TEXT NOT NULL,
                dialect TEXT NOT NULL,
                payload_length INTEGER NOT NULL,
                crc_extra INTEGER NOT NULL,
                source_file TEXT NOT NULL,
                notes TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS field_definitions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                definition_id INTEGER NOT NULL,
                field_name TEXT NOT NULL,
                value_type TEXT NOT NULL,
                array_length INTEGER NOT NULL,
                payload_offset INTEGER NOT NULL,
                unit TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (definition_id) REFERENCES message_definitions(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_mavlink_messages_log ON mavlink_messages(log_file_id);
            CREATE INDEX IF NOT EXISTS ix_mavlink_messages_type ON mavlink_messages(message_type_id);
            CREATE INDEX IF NOT EXISTS ix_message_fields_message ON message_fields(message_id);
            CREATE INDEX IF NOT EXISTS ix_message_fields_name ON message_fields(field_name);
            CREATE INDEX IF NOT EXISTS ix_field_definitions_definition ON field_definitions(definition_id);
            """, "schema");

        TryAddColumn(connection, "message_types", "dialect", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(connection, "mavlink_messages", "route", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(connection, "mavlink_messages", "packet_time_ms", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "mavlink_messages", "packet_timestamp", "TEXT NOT NULL DEFAULT ''");
        ExecuteNonQuery(connection, """
            UPDATE mavlink_messages
            SET packet_time_ms = packet_index
            WHERE packet_time_ms = 0 AND packet_index <> 0;

            UPDATE mavlink_messages
            SET packet_timestamp = (
                SELECT log_files.imported_at
                FROM log_files
                WHERE log_files.id = mavlink_messages.log_file_id
            )
            WHERE packet_timestamp = '';
            """, "backfill message timestamps");
        Log("SQL", $"schema ready: {DatabasePath}");
    }

    public async Task<ImportResult> ImportLogAsync(string path)
    {
        Log("IMPORT", $"read file {path}");
        var bytes = await File.ReadAllBytesAsync(path);
        var frames = MavlinkParser.Parse(bytes);
        var definitions = GetFieldDefinitionsByMessageId();
        var packetTimings = OLogDebugSidecar.LoadPacketTimings(path);
        if (packetTimings.Count > 0)
        {
            Log("IMPORT", $"loaded OSD replay sidecar timings: {packetTimings.Count} packets");
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var importStarted = DateTimeOffset.Now;
        var logId = InsertLogFile(connection, path, frames.Count, importStarted);
        var writtenFields = 0;
        foreach (var frame in frames)
        {
            var messageTypeId = EnsureMessageType(connection, frame.MessageId);
            packetTimings.TryGetValue(frame.PacketIndex, out var timing);
            var messageId = InsertMessage(connection, logId, messageTypeId, frame, importStarted, timing);
            definitions.TryGetValue(frame.MessageId, out var fieldDefinitions);

            foreach (var field in DynamicMavlinkDecoder.Decode(frame, fieldDefinitions ?? []))
            {
                InsertField(connection, messageId, field);
                writtenFields++;
            }
        }

        transaction.Commit();
        Log("SQL WRITE", $"transaction committed: {frames.Count} messages, {writtenFields} fields");
        return new ImportResult(logId, frames.Count, DatabasePath);
    }

    public int ImportDefinitions(IReadOnlyList<LoadedMavlinkDefinition> definitions)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var definition in definitions)
        {
            var definitionId = UpsertDefinition(connection, definition.Message);
            DeleteDefinitionFields(connection, definitionId);
            foreach (var field in definition.Fields)
            {
                InsertFieldDefinition(connection, definitionId, field);
            }

            EnsureMessageType(connection, definition.Message.MessageId);
        }

        transaction.Commit();
        Log("SQL WRITE", $"imported {definitions.Count} generated MAVLink definitions");
        return definitions.Count;
    }

    public IReadOnlyList<LogFileRecord> GetLogs()
    {
        Log("SQL READ", "SELECT log_files ORDER BY imported_at DESC");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, file_name, path, imported_at, message_count
            FROM log_files
            ORDER BY imported_at DESC, id DESC;
            """;
        using var reader = command.ExecuteReader();
        var records = new List<LogFileRecord>();
        while (reader.Read())
        {
            records.Add(new LogFileRecord
            {
                Id = reader.GetInt64(0),
                FileName = reader.GetString(1),
                Path = reader.GetString(2),
                ImportedAt = reader.GetString(3),
                MessageCount = reader.GetInt32(4)
            });
        }

        return records;
    }

    public IReadOnlyList<MavlinkMessageRecord> GetMessages(long logId)
    {
        Log("SQL JOIN", $"mavlink_messages JOIN message_types WHERE log_file_id={logId}");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.packet_index, m.byte_offset, m.mavlink_version, m.sequence,
                   m.packet_time_ms, m.packet_timestamp,
                   m.system_id, m.component_id, t.message_id, t.name, m.route, t.dialect,
                   m.payload_length, m.checksum
            FROM mavlink_messages m
            JOIN message_types t ON t.id = m.message_type_id
            WHERE m.log_file_id = $logId
            ORDER BY m.packet_index
            LIMIT 5000;
            """;
        command.Parameters.AddWithValue("$logId", logId);
        using var reader = command.ExecuteReader();
        var records = new List<MavlinkMessageRecord>();
        while (reader.Read())
        {
            records.Add(new MavlinkMessageRecord
            {
                Id = reader.GetInt64(0),
                PacketIndex = reader.GetInt32(1),
                ByteOffset = reader.GetInt32(2),
                Version = reader.GetInt32(3),
                Sequence = reader.GetInt32(4),
                TimeMs = reader.GetInt64(5),
                Timestamp = reader.GetString(6),
                SystemId = reader.GetInt32(7),
                ComponentId = reader.GetInt32(8),
                MessageId = reader.GetInt32(9),
                MessageName = reader.GetString(10),
                Route = reader.GetString(11),
                Dialect = reader.GetString(12),
                PayloadLength = reader.GetInt32(13),
                Checksum = reader.GetString(14)
            });
        }

        return records;
    }

    public IReadOnlyList<LogVariableRecord> GetLogVariables(long logId)
    {
        Log("SQL JOIN", $"message_fields JOIN mavlink_messages JOIN message_types WHERE log_file_id={logId}");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, m.id, m.packet_index, m.packet_time_ms, m.packet_timestamp,
                   m.route, t.dialect, m.system_id, m.component_id, t.message_id,
                   t.name, f.field_name, f.value_text, f.numeric_value, f.unit
            FROM message_fields f
            JOIN mavlink_messages m ON m.id = f.message_id
            JOIN message_types t ON t.id = m.message_type_id
            WHERE m.log_file_id = $logId
            ORDER BY m.packet_time_ms, m.packet_index, t.message_id, f.field_name
            LIMIT 20000;
            """;
        command.Parameters.AddWithValue("$logId", logId);
        using var reader = command.ExecuteReader();
        var records = new List<LogVariableRecord>();
        while (reader.Read())
        {
            records.Add(new LogVariableRecord
            {
                FieldId = reader.GetInt64(0),
                MessageRowId = reader.GetInt64(1),
                PacketIndex = reader.GetInt32(2),
                TimeMs = reader.GetInt64(3),
                Timestamp = reader.GetString(4),
                Route = reader.GetString(5),
                Dialect = reader.GetString(6),
                SystemId = reader.GetInt32(7),
                ComponentId = reader.GetInt32(8),
                MessageId = reader.GetInt32(9),
                MessageName = reader.GetString(10),
                FieldName = reader.GetString(11),
                ValueText = reader.GetString(12),
                NumericValue = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                Unit = reader.GetString(14)
            });
        }

        return records;
    }

    public IReadOnlyList<OsdReplayRecord> GetOsdReplayFrames(long logId)
    {
        Log("SQL JOIN", $"OSD replay aggregation WHERE log_file_id={logId}");
        var variables = GetLogVariables(logId);
        var frames = new List<OsdReplayRecord>();

        foreach (var group in variables.GroupBy(v => new { v.TimeMs, v.Timestamp }).OrderBy(g => g.Key.TimeMs))
        {
            var values = group.ToDictionary(
                v => $"{v.Route}.{v.MessageName}.{v.FieldName}",
                v => v.ValueText,
                StringComparer.OrdinalIgnoreCase);

            var frame = new OsdReplayRecord
            {
                TimeMs = group.Key.TimeMs,
                Timestamp = group.Key.Timestamp,
                LinkQuality = Lookup(values, "OpenHD Ground.OPENHD_STATS_MONITOR_MODE_WIFI_CARD.rx_signal_quality_adapter"),
                Rssi = Lookup(values, "OpenHD Ground.OPENHD_STATS_MONITOR_MODE_WIFI_CARD.rx_rssi"),
                Snr = Lookup(values, "OpenHD Ground.OPENHD_STATS_MONITOR_MODE_WIFI_CARD.rx_snr_antenna1"),
                AirVideoBitrate = FormatBitrate(Lookup(values, "OpenHD Air.OPENHD_STATS_WB_VIDEO_AIR.curr_measured_encoder_bitrate")),
                InjectedBitrate = FormatBitrate(Lookup(values, "OpenHD Air.OPENHD_STATS_WB_VIDEO_AIR.curr_injected_bitrate")),
                TxPps = Lookup(values, "OpenHD Ground.OPENHD_STATS_TELEMETRY.curr_tx_pps"),
                RxPps = Lookup(values, "OpenHD Ground.OPENHD_STATS_TELEMETRY.curr_rx_pps"),
                PacketLoss = Lookup(values, "OpenHD Ground.OPENHD_STATS_TELEMETRY.curr_rx_packet_loss_perc"),
                DroppedFrames = Lookup(values, "OpenHD Air.OPENHD_STATS_WB_VIDEO_AIR.curr_dropped_frames"),
                CardTemperature = Lookup(values, "OpenHD Ground.OPENHD_STATS_MONITOR_MODE_WIFI_CARD.card_temperature")
            };

            if (HasOsdValue(frame))
            {
                frames.Add(frame);
            }
        }

        return frames;
    }

    public IReadOnlyList<MessageFieldRecord> GetFields(long messageId)
    {
        Log("SQL JOIN", $"message_fields JOIN mavlink_messages JOIN message_types WHERE message_id={messageId}");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.message_id, t.name, f.field_name, f.value_text, f.numeric_value, f.unit
            FROM message_fields f
            JOIN mavlink_messages m ON m.id = f.message_id
            JOIN message_types t ON t.id = m.message_type_id
            WHERE f.message_id = $messageId
            ORDER BY f.id;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        using var reader = command.ExecuteReader();
        var records = new List<MessageFieldRecord>();
        while (reader.Read())
        {
            records.Add(new MessageFieldRecord
            {
                Id = reader.GetInt64(0),
                MessageId = reader.GetInt64(1),
                MessageName = reader.GetString(2),
                FieldName = reader.GetString(3),
                ValueText = reader.GetString(4),
                NumericValue = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                Unit = reader.GetString(6)
            });
        }

        return records;
    }

    public IReadOnlyList<UserVariableRecord> GetVariables()
    {
        Log("SQL READ", "SELECT user_variables");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, value_text, data_type, notes
            FROM user_variables
            ORDER BY name COLLATE NOCASE, id;
            """;
        using var reader = command.ExecuteReader();
        var records = new List<UserVariableRecord>();
        while (reader.Read())
        {
            records.Add(new UserVariableRecord
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ValueText = reader.GetString(2),
                DataType = reader.GetString(3),
                Notes = reader.GetString(4)
            });
        }

        return records;
    }

    public IReadOnlyList<MavlinkMessageDefinitionRecord> GetDefinitions()
    {
        Log("SQL READ", "SELECT message_definitions");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, message_id, name, dialect, payload_length, crc_extra, source_file, notes
            FROM message_definitions
            ORDER BY dialect COLLATE NOCASE, message_id;
            """;
        using var reader = command.ExecuteReader();
        var records = new List<MavlinkMessageDefinitionRecord>();
        while (reader.Read())
        {
            records.Add(new MavlinkMessageDefinitionRecord
            {
                Id = reader.GetInt64(0),
                MessageId = reader.GetInt32(1),
                Name = reader.GetString(2),
                Dialect = reader.GetString(3),
                PayloadLength = reader.GetInt32(4),
                CrcExtra = reader.GetInt32(5),
                SourceFile = reader.GetString(6),
                Notes = reader.GetString(7)
            });
        }

        return records;
    }

    public IReadOnlyList<MavlinkFieldDefinitionRecord> GetDefinitionFields(long definitionId)
    {
        Log("SQL READ", $"SELECT field_definitions WHERE definition_id={definitionId}");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, definition_id, field_name, value_type, array_length, payload_offset, unit, description
            FROM field_definitions
            WHERE definition_id = $definitionId
            ORDER BY payload_offset, id;
            """;
        command.Parameters.AddWithValue("$definitionId", definitionId);
        using var reader = command.ExecuteReader();
        var records = new List<MavlinkFieldDefinitionRecord>();
        while (reader.Read())
        {
            records.Add(new MavlinkFieldDefinitionRecord
            {
                Id = reader.GetInt64(0),
                DefinitionId = reader.GetInt64(1),
                FieldName = reader.GetString(2),
                ValueType = reader.GetString(3),
                ArrayLength = reader.GetInt32(4),
                PayloadOffset = reader.GetInt32(5),
                Unit = reader.GetString(6),
                Description = reader.GetString(7)
            });
        }

        return records;
    }

    public void SaveDefinition(MavlinkMessageDefinitionRecord definition)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE message_definitions
            SET message_id = $messageId,
                name = $name,
                dialect = $dialect,
                payload_length = $length,
                crc_extra = $crc,
                source_file = $sourceFile,
                notes = $notes
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$messageId", definition.MessageId);
        command.Parameters.AddWithValue("$name", definition.Name.Trim());
        command.Parameters.AddWithValue("$dialect", definition.Dialect.Trim());
        command.Parameters.AddWithValue("$length", definition.PayloadLength);
        command.Parameters.AddWithValue("$crc", definition.CrcExtra);
        command.Parameters.AddWithValue("$sourceFile", definition.SourceFile.Trim());
        command.Parameters.AddWithValue("$notes", definition.Notes.Trim());
        command.Parameters.AddWithValue("$id", definition.Id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"UPDATE message_definitions id={definition.Id}");
    }

    public void SaveDefinitionField(MavlinkFieldDefinitionRecord field)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE field_definitions
            SET field_name = $name,
                value_type = $type,
                array_length = $arrayLength,
                payload_offset = $offset,
                unit = $unit,
                description = $description
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$name", field.FieldName.Trim());
        command.Parameters.AddWithValue("$type", field.ValueType.Trim());
        command.Parameters.AddWithValue("$arrayLength", field.ArrayLength);
        command.Parameters.AddWithValue("$offset", field.PayloadOffset);
        command.Parameters.AddWithValue("$unit", field.Unit.Trim());
        command.Parameters.AddWithValue("$description", field.Description.Trim());
        command.Parameters.AddWithValue("$id", field.Id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"UPDATE field_definitions id={field.Id}");
    }

    public void DeleteDefinition(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM message_definitions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"DELETE message_definitions id={id}");
    }

    public void DeleteDefinitionField(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM field_definitions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"DELETE field_definitions id={id}");
    }

    public void SaveField(MessageFieldRecord field)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE message_fields
            SET field_name = $name,
                value_text = $value,
                numeric_value = $numericValue,
                unit = $unit
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$name", field.FieldName.Trim());
        command.Parameters.AddWithValue("$value", field.ValueText.Trim());
        command.Parameters.AddWithValue("$numericValue", (object?)field.NumericValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$unit", field.Unit.Trim());
        command.Parameters.AddWithValue("$id", field.Id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"UPDATE message_fields id={field.Id}");
    }

    public void DeleteField(long fieldId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM message_fields WHERE id = $id;";
        command.Parameters.AddWithValue("$id", fieldId);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"DELETE message_fields id={fieldId}");
    }

    public long SaveVariable(UserVariableRecord variable)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (variable.Id == 0)
        {
            command.CommandText = """
                INSERT INTO user_variables (name, value_text, data_type, notes)
                VALUES ($name, $value, $type, $notes)
                RETURNING id;
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE user_variables
                SET name = $name,
                    value_text = $value,
                    data_type = $type,
                    notes = $notes
                WHERE id = $id
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$id", variable.Id);
        }

        command.Parameters.AddWithValue("$name", variable.Name.Trim());
        command.Parameters.AddWithValue("$value", variable.ValueText.Trim());
        command.Parameters.AddWithValue("$type", string.IsNullOrWhiteSpace(variable.DataType) ? "text" : variable.DataType.Trim());
        command.Parameters.AddWithValue("$notes", variable.Notes.Trim());
        var id = (long)command.ExecuteScalar()!;
        Log("SQL WRITE", variable.Id == 0 ? $"INSERT user_variables id={id}" : $"UPDATE user_variables id={id}");
        return id;
    }

    public void DeleteVariable(long variableId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM user_variables WHERE id = $id;";
        command.Parameters.AddWithValue("$id", variableId);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"DELETE user_variables id={variableId}");
    }

    public void DeleteLog(long logId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM log_files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", logId);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"DELETE log_files id={logId} CASCADE messages/fields");
    }

    private Dictionary<int, IReadOnlyList<MavlinkFieldDefinitionRecord>> GetFieldDefinitionsByMessageId()
    {
        Log("SQL JOIN", "message_definitions JOIN field_definitions for decoder");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.message_id, f.id, f.definition_id, f.field_name, f.value_type,
                   f.array_length, f.payload_offset, f.unit, f.description
            FROM message_definitions d
            JOIN field_definitions f ON f.definition_id = d.id
            ORDER BY d.message_id, f.payload_offset, f.id;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<int, List<MavlinkFieldDefinitionRecord>>();
        while (reader.Read())
        {
            var messageId = reader.GetInt32(0);
            if (!result.TryGetValue(messageId, out var fields))
            {
                fields = [];
                result[messageId] = fields;
            }

            fields.Add(new MavlinkFieldDefinitionRecord
            {
                Id = reader.GetInt64(1),
                DefinitionId = reader.GetInt64(2),
                FieldName = reader.GetString(3),
                ValueType = reader.GetString(4),
                ArrayLength = reader.GetInt32(5),
                PayloadOffset = reader.GetInt32(6),
                Unit = reader.GetString(7),
                Description = reader.GetString(8)
            });
        }

        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<MavlinkFieldDefinitionRecord>)pair.Value);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();

        return connection;
    }

    private void ExecuteNonQuery(SqliteConnection connection, string sql, string category)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        Log("SQL WRITE", category);
    }

    private void TryAddColumn(SqliteConnection connection, string table, string column, string definition)
    {
        try
        {
            ExecuteNonQuery(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", $"ALTER TABLE {table} ADD COLUMN {column}");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            Log("SQL", $"column exists: {table}.{column}");
        }
    }

    private long InsertLogFile(SqliteConnection connection, string path, int messageCount, DateTimeOffset importedAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO log_files (file_name, path, imported_at, message_count)
            VALUES ($fileName, $path, $importedAt, $messageCount)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$fileName", Path.GetFileName(path));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$importedAt", importedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        command.Parameters.AddWithValue("$messageCount", messageCount);
        return (long)command.ExecuteScalar()!;
    }

    private static long EnsureMessageType(SqliteConnection connection, int messageId)
    {
        var definition = GetDefinition(connection, messageId);
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO message_types (message_id, name, dialect)
            VALUES ($messageId, $name, $dialect)
            ON CONFLICT(message_id) DO UPDATE SET
                name = excluded.name,
                dialect = excluded.dialect
            RETURNING id;
            """;
        insert.Parameters.AddWithValue("$messageId", messageId);
        insert.Parameters.AddWithValue("$name", definition?.Name ?? MavlinkMessageDecoder.GetMessageName(messageId));
        insert.Parameters.AddWithValue("$dialect", definition?.Dialect ?? "");
        return (long)insert.ExecuteScalar()!;
    }

    private static MavlinkMessageDefinitionRecord? GetDefinition(SqliteConnection connection, int messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, message_id, name, dialect, payload_length, crc_extra, source_file, notes
            FROM message_definitions
            WHERE message_id = $messageId;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MavlinkMessageDefinitionRecord
        {
            Id = reader.GetInt64(0),
            MessageId = reader.GetInt32(1),
            Name = reader.GetString(2),
            Dialect = reader.GetString(3),
            PayloadLength = reader.GetInt32(4),
            CrcExtra = reader.GetInt32(5),
            SourceFile = reader.GetString(6),
            Notes = reader.GetString(7)
        };
    }

    private static long InsertMessage(SqliteConnection connection, long logId, long messageTypeId, MavlinkFrame frame, DateTimeOffset importStarted, PacketTiming? timing)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mavlink_messages
                (log_file_id, message_type_id, packet_index, packet_time_ms, packet_timestamp,
                 byte_offset, mavlink_version, sequence,
                 system_id, component_id, route, payload_length, checksum, raw_packet_hex)
            VALUES
                ($logId, $messageTypeId, $packetIndex, $packetTimeMs, $packetTimestamp,
                 $byteOffset, $version, $sequence,
                 $systemId, $componentId, $route, $payloadLength, $checksum, $rawPacketHex)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$logId", logId);
        command.Parameters.AddWithValue("$messageTypeId", messageTypeId);
        command.Parameters.AddWithValue("$packetIndex", frame.PacketIndex);
        command.Parameters.AddWithValue("$packetTimeMs", timing?.ElapsedMs ?? frame.PacketIndex);
        command.Parameters.AddWithValue("$packetTimestamp", timing?.Timestamp ?? importStarted.AddMilliseconds(frame.PacketIndex).ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        command.Parameters.AddWithValue("$byteOffset", frame.ByteOffset);
        command.Parameters.AddWithValue("$version", frame.Version);
        command.Parameters.AddWithValue("$sequence", frame.Sequence);
        command.Parameters.AddWithValue("$systemId", frame.SystemId);
        command.Parameters.AddWithValue("$componentId", frame.ComponentId);
        command.Parameters.AddWithValue("$route", RouteFor(frame.SystemId));
        command.Parameters.AddWithValue("$payloadLength", frame.Payload.Length);
        command.Parameters.AddWithValue("$checksum", $"0x{frame.Checksum:X4}");
        command.Parameters.AddWithValue("$rawPacketHex", Convert.ToHexString(frame.RawPacket));
        return (long)command.ExecuteScalar()!;
    }

    private static string RouteFor(int systemId)
    {
        return systemId switch
        {
            100 => "OpenHD Ground",
            101 => "OpenHD Air",
            255 => "QOpenHD",
            1 or 0 => "Flight Controller",
            _ => $"System {systemId}"
        };
    }

    private static void InsertField(SqliteConnection connection, long messageId, DecodedField field)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO message_fields (message_id, field_name, value_text, numeric_value, unit)
            VALUES ($messageId, $name, $value, $numericValue, $unit);
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        command.Parameters.AddWithValue("$name", field.Name);
        command.Parameters.AddWithValue("$value", field.ValueText);
        command.Parameters.AddWithValue("$numericValue", (object?)field.NumericValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$unit", field.Unit);
        command.ExecuteNonQuery();
    }

    private static long UpsertDefinition(SqliteConnection connection, MavlinkMessageDefinitionRecord definition)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO message_definitions
                (message_id, name, dialect, payload_length, crc_extra, source_file, notes)
            VALUES
                ($messageId, $name, $dialect, $payloadLength, $crcExtra, $sourceFile, $notes)
            ON CONFLICT(message_id) DO UPDATE SET
                name = excluded.name,
                dialect = excluded.dialect,
                payload_length = excluded.payload_length,
                crc_extra = excluded.crc_extra,
                source_file = excluded.source_file
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$messageId", definition.MessageId);
        command.Parameters.AddWithValue("$name", definition.Name);
        command.Parameters.AddWithValue("$dialect", definition.Dialect);
        command.Parameters.AddWithValue("$payloadLength", definition.PayloadLength);
        command.Parameters.AddWithValue("$crcExtra", definition.CrcExtra);
        command.Parameters.AddWithValue("$sourceFile", definition.SourceFile);
        command.Parameters.AddWithValue("$notes", definition.Notes);
        return (long)command.ExecuteScalar()!;
    }

    private static void DeleteDefinitionFields(SqliteConnection connection, long definitionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM field_definitions WHERE definition_id = $definitionId;";
        command.Parameters.AddWithValue("$definitionId", definitionId);
        command.ExecuteNonQuery();
    }

    private static void InsertFieldDefinition(SqliteConnection connection, long definitionId, MavlinkFieldDefinitionRecord field)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO field_definitions
                (definition_id, field_name, value_type, array_length, payload_offset, unit, description)
            VALUES
                ($definitionId, $fieldName, $valueType, $arrayLength, $payloadOffset, $unit, $description);
            """;
        command.Parameters.AddWithValue("$definitionId", definitionId);
        command.Parameters.AddWithValue("$fieldName", field.FieldName);
        command.Parameters.AddWithValue("$valueType", field.ValueType);
        command.Parameters.AddWithValue("$arrayLength", field.ArrayLength);
        command.Parameters.AddWithValue("$payloadOffset", field.PayloadOffset);
        command.Parameters.AddWithValue("$unit", field.Unit);
        command.Parameters.AddWithValue("$description", field.Description);
        command.ExecuteNonQuery();
    }

    private void Log(string category, string detail)
    {
        debug?.Invoke(new DebugEventRecord
        {
            Timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
            Category = category,
            Detail = detail
        });
    }

    private static string Lookup(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) ? value : "-";
    }

    private static string FormatBitrate(string value)
    {
        return double.TryParse(value, out var bitrate) ? $"{bitrate / 1_000_000:0.00} Mbps" : value;
    }

    private static bool HasOsdValue(OsdReplayRecord frame)
    {
        return frame.LinkQuality != "-"
            || frame.Rssi != "-"
            || frame.Snr != "-"
            || frame.AirVideoBitrate != "-"
            || frame.InjectedBitrate != "-"
            || frame.TxPps != "-"
            || frame.RxPps != "-"
            || frame.PacketLoss != "-"
            || frame.DroppedFrames != "-"
            || frame.CardTemperature != "-";
    }
}

public sealed record ImportResult(long LogId, int MessageCount, string DatabasePath);
