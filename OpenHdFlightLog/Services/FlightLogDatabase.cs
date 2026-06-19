using System.ComponentModel;
using System.Diagnostics;
using MySqlConnector;
using OpenHdFlightLog.Models;

namespace OpenHdFlightLog.Services;

public sealed class FlightLogDatabase
{
    // Optionaler Debug-Hook in Richtung ViewModel. Der Datenbankservice bleibt auch ohne
    // UI verwendbar, kann aber bei Bedarf jeden wichtigen SQL-/Import-Schritt melden.
    private readonly Action<DebugEventRecord>? debug;

    public string DatabasePath { get; }

    private readonly MySqlConnectionStringBuilder connectionStringBuilder;

    public FlightLogDatabase(Action<DebugEventRecord>? debug = null)
    {
        this.debug = debug;
        connectionStringBuilder = CreateConnectionStringBuilder();
        DatabasePath = $"{connectionStringBuilder.Server}:{connectionStringBuilder.Port}/{connectionStringBuilder.Database}";
        MySqlServerManager.EnsureServerStarted(connectionStringBuilder, Log);
        EnsureDatabase();
        EnsureSchema();
    }

    private static MySqlConnectionStringBuilder CreateConnectionStringBuilder()
    {
        return new MySqlConnectionStringBuilder
        {
            Server = Environment.GetEnvironmentVariable("OPENHD_MYSQL_HOST") ?? "127.0.0.1",
            Port = uint.TryParse(Environment.GetEnvironmentVariable("OPENHD_MYSQL_PORT"), out var port) ? port : 13306,
            UserID = Environment.GetEnvironmentVariable("OPENHD_MYSQL_USER") ?? "root",
            Password = Environment.GetEnvironmentVariable("OPENHD_MYSQL_PASSWORD") ?? "openhd",
            Database = Environment.GetEnvironmentVariable("OPENHD_MYSQL_DATABASE") ?? "openhd_flightlog",
            CharacterSet = "utf8mb4",
            SslMode = MySqlSslMode.None,
            AllowLoadLocalInfile = false
        };
    }

    private void EnsureDatabase()
    {
        var serverConnection = new MySqlConnectionStringBuilder(connectionStringBuilder.ConnectionString)
        {
            Database = ""
        };

        using var connection = new MySqlConnection(serverConnection.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE IF NOT EXISTS `{connectionStringBuilder.Database.Replace("`", "``")}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;";
        command.ExecuteNonQuery();
        Log("MYSQL", $"database ready: {connectionStringBuilder.Database}");
    }

    public void EnsureSchema()
    {
        // EnsureSchema ist idempotent: Die Methode darf bei jedem Start laufen. Neue
        // Tabellen werden angelegt, vorhandene bleiben erhalten.
        using var connection = OpenConnection();
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS log_files (
                id BIGINT PRIMARY KEY AUTO_INCREMENT,
                file_name VARCHAR(512) NOT NULL,
                path TEXT NOT NULL,
                imported_at VARCHAR(64) NOT NULL,
                message_count INT NOT NULL
            ) ENGINE=InnoDB;

            CREATE TABLE IF NOT EXISTS message_types (
                id BIGINT PRIMARY KEY AUTO_INCREMENT,
                message_id INT NOT NULL UNIQUE,
                name VARCHAR(255) NOT NULL,
                dialect VARCHAR(255) NOT NULL DEFAULT '',
                description VARCHAR(1024) NOT NULL DEFAULT ''
            ) ENGINE=InnoDB;

            CREATE TABLE IF NOT EXISTS mavlink_messages (
                id BIGINT PRIMARY KEY AUTO_INCREMENT,
                log_file_id BIGINT NOT NULL,
                message_type_id BIGINT NOT NULL,
                packet_index INT NOT NULL,
                packet_time_ms BIGINT NOT NULL DEFAULT 0,
                packet_timestamp VARCHAR(64) NOT NULL DEFAULT '',
                byte_offset INT NOT NULL,
                mavlink_version INT NOT NULL,
                sequence INT NOT NULL,
                system_id INT NOT NULL,
                component_id INT NOT NULL,
                route VARCHAR(255) NOT NULL DEFAULT '',
                payload_length INT NOT NULL,
                checksum VARCHAR(32) NOT NULL,
                raw_packet_hex LONGTEXT NOT NULL,
                FOREIGN KEY (log_file_id) REFERENCES log_files(id) ON DELETE CASCADE,
                FOREIGN KEY (message_type_id) REFERENCES message_types(id)
            ) ENGINE=InnoDB;

            CREATE TABLE IF NOT EXISTS message_fields (
                id BIGINT PRIMARY KEY AUTO_INCREMENT,
                message_id BIGINT NOT NULL,
                field_name VARCHAR(255) NOT NULL,
                value_text TEXT NOT NULL,
                numeric_value DOUBLE NULL,
                unit VARCHAR(64) NOT NULL DEFAULT '',
                FOREIGN KEY (message_id) REFERENCES mavlink_messages(id) ON DELETE CASCADE
            ) ENGINE=InnoDB;

            CREATE TABLE IF NOT EXISTS user_variables (
                id BIGINT PRIMARY KEY AUTO_INCREMENT,
                name VARCHAR(255) NOT NULL,
                value_text TEXT NOT NULL,
                data_type VARCHAR(64) NOT NULL DEFAULT 'text',
                notes TEXT NOT NULL
            ) ENGINE=InnoDB;

            CREATE TABLE IF NOT EXISTS message_definitions (
                id BIGINT PRIMARY KEY AUTO_INCREMENT,
                message_id INT NOT NULL UNIQUE,
                name VARCHAR(255) NOT NULL,
                dialect VARCHAR(255) NOT NULL,
                payload_length INT NOT NULL,
                crc_extra INT NOT NULL,
                source_file VARCHAR(1024) NOT NULL,
                notes TEXT NOT NULL
            ) ENGINE=InnoDB;

            CREATE TABLE IF NOT EXISTS field_definitions (
                id BIGINT PRIMARY KEY AUTO_INCREMENT,
                definition_id BIGINT NOT NULL,
                field_name VARCHAR(255) NOT NULL,
                value_type VARCHAR(64) NOT NULL,
                array_length INT NOT NULL,
                payload_offset INT NOT NULL,
                unit VARCHAR(64) NOT NULL DEFAULT '',
                description TEXT NOT NULL,
                FOREIGN KEY (definition_id) REFERENCES message_definitions(id) ON DELETE CASCADE
            ) ENGINE=InnoDB;
            """, "schema");

        EnsureIndex(connection, "ix_mavlink_messages_log", "mavlink_messages", "log_file_id");
        EnsureIndex(connection, "ix_mavlink_messages_type", "mavlink_messages", "message_type_id");
        EnsureIndex(connection, "ix_message_fields_message", "message_fields", "message_id");
        EnsureIndex(connection, "ix_message_fields_name", "message_fields", "field_name");
        EnsureIndex(connection, "ix_field_definitions_definition", "field_definitions", "definition_id");

        // Einfache Migrationen fuer bestehende lokale Datenbanken. MySQL kann Spalten
        // mit ALTER TABLE ADD COLUMN ergaenzen; existiert die Spalte bereits, wird der
        // bekannte Fehler abgefangen und nur im Debug-Log notiert.
        TryAddColumn(connection, "message_types", "dialect", "VARCHAR(255) NOT NULL DEFAULT ''");
        TryAddColumn(connection, "mavlink_messages", "route", "VARCHAR(255) NOT NULL DEFAULT ''");
        TryAddColumn(connection, "mavlink_messages", "packet_time_ms", "BIGINT NOT NULL DEFAULT 0");
        TryAddColumn(connection, "mavlink_messages", "packet_timestamp", "VARCHAR(64) NOT NULL DEFAULT ''");
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

    public ImportResult SaveImportedLog(string path, IReadOnlyList<ImportedMavlinkMessage> messages)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Die Datenhaltungsschicht bekommt bereits vorbereitete Nachrichten. Sie parst
        // und dekodiert nicht selbst, sondern speichert den Import atomar in MySQL.
        var importStarted = DateTimeOffset.Now;
        var logId = InsertLogFile(connection, transaction, path, messages.Count, importStarted);
        var writtenFields = 0;
        foreach (var message in messages)
        {
            var frame = message.Frame;
            var messageTypeId = EnsureMessageType(connection, transaction, frame.MessageId, message.MessageName, message.Dialect);
            var messageId = InsertMessage(connection, transaction, logId, messageTypeId, message, importStarted);

            foreach (var field in message.Fields)
            {
                InsertField(connection, transaction, messageId, field);
                writtenFields++;
            }
        }

        transaction.Commit();
        Log("SQL WRITE", $"transaction committed: {messages.Count} messages, {writtenFields} fields");
        return new ImportResult(logId, messages.Count, DatabasePath);
    }

    public int ImportDefinitions(IReadOnlyList<LoadedMavlinkDefinition> definitions)
    {
        // Definitionen werden als Upsert importiert. Dadurch kann derselbe OpenHD-
        // Headerstand erneut geladen werden, ohne doppelte message_definitions zu erzeugen.
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var definition in definitions)
        {
            var definitionId = UpsertDefinition(connection, transaction, definition.Message);
            // Felddefinitionen werden komplett ersetzt, weil sich Offsets oder Typen
            // zwischen Header-Versionen aendern koennen.
            DeleteDefinitionFields(connection, transaction, definitionId);
            foreach (var field in definition.Fields)
            {
                InsertFieldDefinition(connection, transaction, definitionId, field);
            }

            EnsureMessageType(connection, transaction, definition.Message.MessageId, definition.Message.Name, definition.Message.Dialect);
        }

        transaction.Commit();
        Log("SQL WRITE", $"imported {definitions.Count} generated MAVLink definitions");
        return definitions.Count;
    }

    public IReadOnlyList<LogFileRecord> GetLogs()
    {
        // Read-Methoden geben einfache Record-Objekte zurueck. Sie halten keine offene
        // Datenbankverbindung; die UI arbeitet danach nur noch mit den kopierten Werten.
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
            WHERE m.log_file_id = @logId
            ORDER BY m.packet_index
            LIMIT 5000;
            """;
        command.Parameters.AddWithValue("@logId", logId);
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

    public IReadOnlyList<UdpReplayPacket> GetUdpReplayPackets(long logId)
    {
        Log("SQL READ", $"raw MAVLink packets for UDP replay WHERE log_file_id={logId}");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT packet_index, packet_time_ms, raw_packet_hex
            FROM mavlink_messages
            WHERE log_file_id = @logId
            ORDER BY packet_index;
            """;
        command.Parameters.AddWithValue("@logId", logId);
        using var reader = command.ExecuteReader();
        var records = new List<UdpReplayPacket>();
        while (reader.Read())
        {
            records.Add(new UdpReplayPacket
            {
                PacketIndex = reader.GetInt32(0),
                TimeMs = reader.GetInt64(1),
                RawPacket = Convert.FromHexString(reader.GetString(2))
            });
        }

        return records;
    }

    public (long MinTimeMs, long MaxTimeMs, int PacketCount) GetUdpReplayRange(long logId)
    {
        Log("SQL READ", $"UDP replay time range WHERE log_file_id={logId}");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(packet_time_ms), MAX(packet_time_ms), COUNT(*)
            FROM mavlink_messages
            WHERE log_file_id = @logId;
            """;
        command.Parameters.AddWithValue("@logId", logId);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return (0, 0, 0);
        }

        return (reader.GetInt64(0), reader.GetInt64(1), Convert.ToInt32(reader.GetInt64(2)));
    }

    public int RedecodeLogFields(long logId)
    {
        Log("SQL READ", $"re-decode raw MAVLink packets WHERE log_file_id={logId}");
        var definitions = GetFieldDefinitionsByMessageId();
        var messages = new List<(long Id, int MessageId, string RawPacketHex)>();

        using (var readConnection = OpenConnection())
        using (var readCommand = readConnection.CreateCommand())
        {
            readCommand.CommandText = """
                SELECT m.id, t.message_id, m.raw_packet_hex
                FROM mavlink_messages m
                JOIN message_types t ON t.id = m.message_type_id
                WHERE m.log_file_id = @logId
                ORDER BY m.packet_index;
                """;
            readCommand.Parameters.AddWithValue("@logId", logId);
            using var reader = readCommand.ExecuteReader();
            while (reader.Read())
            {
                messages.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2)));
            }
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE f
                FROM message_fields f
                JOIN mavlink_messages m ON m.id = f.message_id
                WHERE m.log_file_id = @logId;
                """;
            delete.Parameters.AddWithValue("@logId", logId);
            delete.ExecuteNonQuery();
        }

        var writtenFields = 0;
        foreach (var message in messages)
        {
            var raw = Convert.FromHexString(message.RawPacketHex);
            var frame = MavlinkParser.Parse(raw).FirstOrDefault();
            if (frame is null)
            {
                continue;
            }

            definitions.TryGetValue(message.MessageId, out var fields);
            foreach (var field in DynamicMavlinkDecoder.Decode(frame, fields ?? []))
            {
                InsertField(connection, transaction, message.Id, field);
                writtenFields++;
            }
        }

        transaction.Commit();
        Log("SQL WRITE", $"re-decoded log {logId}: {writtenFields} fields");
        return writtenFields;
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
            WHERE m.log_file_id = @logId
            ORDER BY m.packet_time_ms, m.packet_index, t.message_id, f.field_name
            LIMIT 20000;
            """;
        command.Parameters.AddWithValue("@logId", logId);
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
            WHERE f.message_id = @messageId
            ORDER BY f.id;
            """;
        command.Parameters.AddWithValue("@messageId", messageId);
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
            ORDER BY name, id;
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
            ORDER BY dialect, message_id;
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
            WHERE definition_id = @definitionId
            ORDER BY payload_offset, id;
            """;
        command.Parameters.AddWithValue("@definitionId", definitionId);
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
            SET message_id = @messageId,
                name = @name,
                dialect = @dialect,
                payload_length = @length,
                crc_extra = @crc,
                source_file = @sourceFile,
                notes = @notes
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@messageId", definition.MessageId);
        command.Parameters.AddWithValue("@name", definition.Name.Trim());
        command.Parameters.AddWithValue("@dialect", definition.Dialect.Trim());
        command.Parameters.AddWithValue("@length", definition.PayloadLength);
        command.Parameters.AddWithValue("@crc", definition.CrcExtra);
        command.Parameters.AddWithValue("@sourceFile", definition.SourceFile.Trim());
        command.Parameters.AddWithValue("@notes", definition.Notes.Trim());
        command.Parameters.AddWithValue("@id", definition.Id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"UPDATE message_definitions id={definition.Id}");
    }

    public void SaveDefinitionField(MavlinkFieldDefinitionRecord field)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE field_definitions
            SET field_name = @name,
                value_type = @type,
                array_length = @arrayLength,
                payload_offset = @offset,
                unit = @unit,
                description = @description
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@name", field.FieldName.Trim());
        command.Parameters.AddWithValue("@type", field.ValueType.Trim());
        command.Parameters.AddWithValue("@arrayLength", field.ArrayLength);
        command.Parameters.AddWithValue("@offset", field.PayloadOffset);
        command.Parameters.AddWithValue("@unit", field.Unit.Trim());
        command.Parameters.AddWithValue("@description", field.Description.Trim());
        command.Parameters.AddWithValue("@id", field.Id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"UPDATE field_definitions id={field.Id}");
    }

    public void DeleteDefinition(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM message_definitions WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"DELETE message_definitions id={id}");
    }

    public void DeleteDefinitionField(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM field_definitions WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"DELETE field_definitions id={id}");
    }

    public void SaveField(MessageFieldRecord field)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE message_fields
            SET field_name = @name,
                value_text = @value,
                numeric_value = @numericValue,
                unit = @unit
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@name", field.FieldName.Trim());
        command.Parameters.AddWithValue("@value", field.ValueText.Trim());
        command.Parameters.AddWithValue("@numericValue", (object?)field.NumericValue ?? DBNull.Value);
        command.Parameters.AddWithValue("@unit", field.Unit.Trim());
        command.Parameters.AddWithValue("@id", field.Id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"UPDATE message_fields id={field.Id}");
    }

    public void DeleteField(long fieldId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM message_fields WHERE id = @id;";
        command.Parameters.AddWithValue("@id", fieldId);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"DELETE message_fields id={fieldId}");
    }

    public long SaveVariable(UserVariableRecord variable)
    {
        // Manuelle Variablen sind unabhaengig von Log-Imports. Id == 0 bedeutet: das
        // Objekt existiert nur in der UI und muss eingefuegt werden.
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (variable.Id == 0)
        {
            command.CommandText = """
                INSERT INTO user_variables (name, value_text, data_type, notes)
                VALUES (@name, @value, @type, @notes)
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE user_variables
                SET name = @name,
                    value_text = @value,
                    data_type = @type,
                    notes = @notes
                WHERE id = @id
                """;
            command.Parameters.AddWithValue("@id", variable.Id);
        }

        command.Parameters.AddWithValue("@name", variable.Name.Trim());
        command.Parameters.AddWithValue("@value", variable.ValueText.Trim());
        command.Parameters.AddWithValue("@type", string.IsNullOrWhiteSpace(variable.DataType) ? "text" : variable.DataType.Trim());
        command.Parameters.AddWithValue("@notes", variable.Notes.Trim());
        command.ExecuteNonQuery();
        var id = variable.Id == 0 ? command.LastInsertedId : variable.Id;
        Log("SQL WRITE", variable.Id == 0 ? $"INSERT user_variables id={id}" : $"UPDATE user_variables id={id}");
        return id;
    }

    public void DeleteVariable(long variableId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM user_variables WHERE id = @id;";
        command.Parameters.AddWithValue("@id", variableId);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"DELETE user_variables id={variableId}");
    }

    public void DeleteLog(long logId)
    {
        // Wegen aktivierter Foreign Keys entfernt MySQL alle mavlink_messages und
        // message_fields zum Log automatisch. Das verhindert verwaiste Detaildaten.
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM log_files WHERE id = @id;";
        command.Parameters.AddWithValue("@id", logId);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"DELETE log_files id={logId} CASCADE messages/fields");
    }

    public Dictionary<int, IReadOnlyList<MavlinkFieldDefinitionRecord>> GetFieldDefinitionsByMessageId()
    {
        // Import-Optimierung: Die Feldlayouts werden einmal geladen und nach MAVLink-
        // Message-ID gruppiert. Danach kann jedes Paket direkt per Dictionary-Lookup
        // dekodiert werden.
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

    private MySqlConnection OpenConnection()
    {
        var connection = new MySqlConnection(connectionStringBuilder.ConnectionString);
        connection.Open();
        return connection;
    }

    private void ExecuteNonQuery(MySqlConnection connection, string sql, string category)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        Log("SQL WRITE", category);
    }

    private void TryAddColumn(MySqlConnection connection, string table, string column, string definition)
    {
        try
        {
            ExecuteNonQuery(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", $"ALTER TABLE {table} ADD COLUMN {column}");
        }
        catch (MySqlException ex) when (ex.Number == 1060)
        {
            // MySQL-Fehler 1060 ist hier der erwartete Fehler fuer "duplicate column".
            Log("SQL", $"column exists: {table}.{column}");
        }
    }

    private void EnsureIndex(MySqlConnection connection, string indexName, string table, string columns)
    {
        try
        {
            ExecuteNonQuery(connection, $"CREATE INDEX {indexName} ON {table}({columns});", $"CREATE INDEX {indexName}");
        }
        catch (MySqlException ex) when (ex.Number == 1061)
        {
            // MySQL-Fehler 1061 ist hier der erwartete Fehler fuer "duplicate key name".
            Log("SQL", $"index exists: {indexName}");
        }
    }

    private long InsertLogFile(MySqlConnection connection, MySqlTransaction transaction, string path, int messageCount, DateTimeOffset importedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO log_files (file_name, path, imported_at, message_count)
            VALUES (@fileName, @path, @importedAt, @messageCount)
            """;
        command.Parameters.AddWithValue("@fileName", Path.GetFileName(path));
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@importedAt", importedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        command.Parameters.AddWithValue("@messageCount", messageCount);
        command.ExecuteNonQuery();
        return command.LastInsertedId;
    }

    private static long EnsureMessageType(MySqlConnection connection, MySqlTransaction transaction, int messageId, string name, string dialect)
    {
        // message_types normalisiert Message-ID, Name und Dialekt. Importierte
        // mavlink_messages referenzieren diese Tabelle, damit Name/Dialekt nicht in
        // jeder Nachricht wiederholt werden muessen.
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO message_types (message_id, name, dialect)
            VALUES (@messageId, @name, @dialect)
            ON DUPLICATE KEY UPDATE
                id = LAST_INSERT_ID(id),
                name = VALUES(name),
                dialect = VALUES(dialect);
            """;
        insert.Parameters.AddWithValue("@messageId", messageId);
        insert.Parameters.AddWithValue("@name", name);
        insert.Parameters.AddWithValue("@dialect", dialect);
        insert.ExecuteNonQuery();
        return insert.LastInsertedId;
    }

    private static long InsertMessage(MySqlConnection connection, MySqlTransaction transaction, long logId, long messageTypeId, ImportedMavlinkMessage message, DateTimeOffset importStarted)
    {
        // Wenn eine Sidecar-Zeitinformation vorhanden ist, wird sie bevorzugt. Sonst
        // nutzt die Anwendung den Paketindex als robuste, monotone Ersatzzeit.
        var frame = message.Frame;
        var timing = message.Timing;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mavlink_messages
                (log_file_id, message_type_id, packet_index, packet_time_ms, packet_timestamp,
                 byte_offset, mavlink_version, sequence,
                 system_id, component_id, route, payload_length, checksum, raw_packet_hex)
            VALUES
                (@logId, @messageTypeId, @packetIndex, @packetTimeMs, @packetTimestamp,
                 @byteOffset, @version, @sequence,
                 @systemId, @componentId, @route, @payloadLength, @checksum, @rawPacketHex)
            """;
        command.Parameters.AddWithValue("@logId", logId);
        command.Parameters.AddWithValue("@messageTypeId", messageTypeId);
        command.Parameters.AddWithValue("@packetIndex", frame.PacketIndex);
        command.Parameters.AddWithValue("@packetTimeMs", timing?.ElapsedMs ?? frame.PacketIndex);
        command.Parameters.AddWithValue("@packetTimestamp", timing?.Timestamp ?? importStarted.AddMilliseconds(frame.PacketIndex).ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        command.Parameters.AddWithValue("@byteOffset", frame.ByteOffset);
        command.Parameters.AddWithValue("@version", frame.Version);
        command.Parameters.AddWithValue("@sequence", frame.Sequence);
        command.Parameters.AddWithValue("@systemId", frame.SystemId);
        command.Parameters.AddWithValue("@componentId", frame.ComponentId);
        command.Parameters.AddWithValue("@route", message.Route);
        command.Parameters.AddWithValue("@payloadLength", frame.Payload.Length);
        command.Parameters.AddWithValue("@checksum", $"0x{frame.Checksum:X4}");
        command.Parameters.AddWithValue("@rawPacketHex", Convert.ToHexString(frame.RawPacket));
        command.ExecuteNonQuery();
        return command.LastInsertedId;
    }

    private static void InsertField(MySqlConnection connection, MySqlTransaction transaction, long messageId, DecodedField field)
    {
        // numeric_value ist nullable: Textfelder und Arrays bleiben nur als value_text
        // erhalten, echte Zahlen koennen zusaetzlich sortiert/gefiltert werden.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_fields (message_id, field_name, value_text, numeric_value, unit)
            VALUES (@messageId, @name, @value, @numericValue, @unit);
            """;
        command.Parameters.AddWithValue("@messageId", messageId);
        command.Parameters.AddWithValue("@name", field.Name);
        command.Parameters.AddWithValue("@value", field.ValueText);
        command.Parameters.AddWithValue("@numericValue", (object?)field.NumericValue ?? DBNull.Value);
        command.Parameters.AddWithValue("@unit", field.Unit);
        command.ExecuteNonQuery();
    }

    private static long UpsertDefinition(MySqlConnection connection, MySqlTransaction transaction, MavlinkMessageDefinitionRecord definition)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_definitions
                (message_id, name, dialect, payload_length, crc_extra, source_file, notes)
            VALUES
                (@messageId, @name, @dialect, @payloadLength, @crcExtra, @sourceFile, @notes)
            ON DUPLICATE KEY UPDATE
                id = LAST_INSERT_ID(id),
                name = VALUES(name),
                dialect = VALUES(dialect),
                payload_length = VALUES(payload_length),
                crc_extra = VALUES(crc_extra),
                source_file = VALUES(source_file);
            """;
        command.Parameters.AddWithValue("@messageId", definition.MessageId);
        command.Parameters.AddWithValue("@name", definition.Name);
        command.Parameters.AddWithValue("@dialect", definition.Dialect);
        command.Parameters.AddWithValue("@payloadLength", definition.PayloadLength);
        command.Parameters.AddWithValue("@crcExtra", definition.CrcExtra);
        command.Parameters.AddWithValue("@sourceFile", definition.SourceFile);
        command.Parameters.AddWithValue("@notes", definition.Notes);
        command.ExecuteNonQuery();
        return command.LastInsertedId;
    }

    private static void DeleteDefinitionFields(MySqlConnection connection, MySqlTransaction transaction, long definitionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM field_definitions WHERE definition_id = @definitionId;";
        command.Parameters.AddWithValue("@definitionId", definitionId);
        command.ExecuteNonQuery();
    }

    private static void InsertFieldDefinition(MySqlConnection connection, MySqlTransaction transaction, long definitionId, MavlinkFieldDefinitionRecord field)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO field_definitions
                (definition_id, field_name, value_type, array_length, payload_offset, unit, description)
            VALUES
                (@definitionId, @fieldName, @valueType, @arrayLength, @payloadOffset, @unit, @description);
            """;
        command.Parameters.AddWithValue("@definitionId", definitionId);
        command.Parameters.AddWithValue("@fieldName", field.FieldName);
        command.Parameters.AddWithValue("@valueType", field.ValueType);
        command.Parameters.AddWithValue("@arrayLength", field.ArrayLength);
        command.Parameters.AddWithValue("@payloadOffset", field.PayloadOffset);
        command.Parameters.AddWithValue("@unit", field.Unit);
        command.Parameters.AddWithValue("@description", field.Description);
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

}

internal static class MySqlServerManager
{
    private const string ContainerName = "openhd-flightlog-mysql";
    private const string ImageName = "mysql:8.4";

    public static void EnsureServerStarted(MySqlConnectionStringBuilder builder, Action<string, string> log)
    {
        if (CanConnect(builder))
        {
            log("MYSQL", $"server reachable: {builder.Server}:{builder.Port}");
            return;
        }

        log("MYSQL", $"server not reachable at {builder.Server}:{builder.Port}; trying Docker");
        RunDocker(log, "start", ContainerName);
        if (WaitForServer(builder, TimeSpan.FromSeconds(45), log))
        {
            return;
        }

        RunDocker(
            log,
            "run",
            "--name", ContainerName,
            "-e", $"MYSQL_ROOT_PASSWORD={builder.Password}",
            "-e", $"MYSQL_DATABASE={builder.Database}",
            "-p", $"{builder.Port}:3306",
            "-d", ImageName);

        if (!WaitForServer(builder, TimeSpan.FromSeconds(120), log))
        {
            throw new InvalidOperationException(
                $"MySQL could not be started automatically. Install/start MySQL on {builder.Server}:{builder.Port}, " +
                "or install Docker so the app can run the openhd-flightlog-mysql container.");
        }
    }

    private static bool WaitForServer(MySqlConnectionStringBuilder builder, TimeSpan timeout, Action<string, string> log)
    {
        var deadline = DateTimeOffset.Now.Add(timeout);
        while (DateTimeOffset.Now < deadline)
        {
            if (CanConnect(builder))
            {
                log("MYSQL", $"server started: {builder.Server}:{builder.Port}");
                return true;
            }

            Thread.Sleep(1000);
        }

        return false;
    }

    private static bool CanConnect(MySqlConnectionStringBuilder builder)
    {
        try
        {
            var serverConnection = new MySqlConnectionStringBuilder(builder.ConnectionString)
            {
                Database = "",
                ConnectionTimeout = 2
            };

            using var connection = new MySqlConnection(serverConnection.ConnectionString);
            connection.Open();
            return true;
        }
        catch (MySqlException)
        {
            return false;
        }
    }

    private static void RunDocker(Action<string, string> log, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                log("MYSQL", "docker command timed out");
                return;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode == 0)
            {
                log("MYSQL", string.IsNullOrWhiteSpace(output) ? "docker command finished" : output);
            }
            else
            {
                log("MYSQL", string.IsNullOrWhiteSpace(error) ? $"docker exit code {process.ExitCode}" : error);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log("MYSQL", $"docker unavailable: {ex.Message}");
        }
    }
}

