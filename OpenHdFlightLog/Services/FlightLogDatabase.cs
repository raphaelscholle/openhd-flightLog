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
                import_started_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
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

            CREATE TABLE IF NOT EXISTS database_activity_log (
                id BIGINT PRIMARY KEY AUTO_INCREMENT,
                changed_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
                table_name VARCHAR(64) NOT NULL,
                activity_type VARCHAR(16) NOT NULL,
                row_id BIGINT NULL,
                summary TEXT NOT NULL
            ) ENGINE=InnoDB;
            """, "schema");

        EnsureIndex(connection, "ix_mavlink_messages_log", "mavlink_messages", "log_file_id");
        EnsureIndex(connection, "ix_mavlink_messages_type", "mavlink_messages", "message_type_id");
        EnsureIndex(connection, "ix_message_fields_message", "message_fields", "message_id");
        EnsureIndex(connection, "ix_message_fields_name", "message_fields", "field_name");
        EnsureIndex(connection, "ix_field_definitions_definition", "field_definitions", "definition_id");
        EnsureIndex(connection, "ix_database_activity_log_changed_at", "database_activity_log", "changed_at");

        // Einfache Migrationen fuer bestehende lokale Datenbanken. MySQL kann Spalten
        // mit ALTER TABLE ADD COLUMN ergaenzen; existiert die Spalte bereits, wird der
        // bekannte Fehler abgefangen und nur im Debug-Log notiert.
        TryAddColumn(connection, "message_types", "dialect", "VARCHAR(255) NOT NULL DEFAULT ''");
        TryAddColumn(connection, "log_files", "import_started_at", "DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) AFTER imported_at");
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
        EnsureDatabaseRoutines(connection);
        Log("SQL", $"schema ready: {DatabasePath}");
    }

    public ImportResult SaveImportedLog(string path, IReadOnlyList<ImportedMavlinkMessage> messages)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Die Datenhaltungsschicht bekommt bereits vorbereitete Nachrichten. MySQL-
        // Prozeduren und Trigger uebernehmen die eigentlichen Schreibregeln.
        var importStarted = DateTimeOffset.Now;
        var logId = InsertLogFile(connection, transaction, path, importStarted);
        var writtenFields = 0;
        foreach (var message in messages)
        {
            var frame = message.Frame;
            var messageTypeId = EnsureMessageType(connection, transaction, frame.MessageId, message.MessageName, message.Dialect);
            var messageId = InsertMessage(connection, transaction, logId, messageTypeId, message);

            foreach (var field in message.Fields)
            {
                InsertField(connection, transaction, messageId, field);
                writtenFields++;
            }
        }

        transaction.Commit();
        Log("SQL WRITE", $"CALL import procedures committed: {messages.Count} messages, {writtenFields} fields");
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
        command.CommandText = "CALL sp_update_message_definition(@id, @messageId, @name, @dialect, @length, @crc, @sourceFile, @notes);";
        command.Parameters.AddWithValue("@messageId", definition.MessageId);
        command.Parameters.AddWithValue("@name", definition.Name.Trim());
        command.Parameters.AddWithValue("@dialect", definition.Dialect.Trim());
        command.Parameters.AddWithValue("@length", definition.PayloadLength);
        command.Parameters.AddWithValue("@crc", definition.CrcExtra);
        command.Parameters.AddWithValue("@sourceFile", definition.SourceFile.Trim());
        command.Parameters.AddWithValue("@notes", definition.Notes.Trim());
        command.Parameters.AddWithValue("@id", definition.Id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"CALL sp_update_message_definition id={definition.Id}");
    }

    public void SaveDefinitionField(MavlinkFieldDefinitionRecord field)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "CALL sp_update_field_definition(@id, @name, @type, @arrayLength, @offset, @unit, @description);";
        command.Parameters.AddWithValue("@name", field.FieldName.Trim());
        command.Parameters.AddWithValue("@type", field.ValueType.Trim());
        command.Parameters.AddWithValue("@arrayLength", field.ArrayLength);
        command.Parameters.AddWithValue("@offset", field.PayloadOffset);
        command.Parameters.AddWithValue("@unit", field.Unit.Trim());
        command.Parameters.AddWithValue("@description", field.Description.Trim());
        command.Parameters.AddWithValue("@id", field.Id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"CALL sp_update_field_definition id={field.Id}");
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
        command.CommandText = "CALL sp_update_message_field(@id, @name, @value, @numericValue, @unit);";
        command.Parameters.AddWithValue("@name", field.FieldName.Trim());
        command.Parameters.AddWithValue("@value", field.ValueText.Trim());
        command.Parameters.AddWithValue("@numericValue", (object?)field.NumericValue ?? DBNull.Value);
        command.Parameters.AddWithValue("@unit", field.Unit.Trim());
        command.Parameters.AddWithValue("@id", field.Id);
        command.ExecuteNonQuery();
        Log("SQL WRITE", $"CALL sp_update_message_field id={field.Id}");
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
        command.CommandText = "CALL sp_save_user_variable(@id, @name, @value, @type, @notes);";
        command.Parameters.AddWithValue("@id", variable.Id);

        command.Parameters.AddWithValue("@name", variable.Name.Trim());
        command.Parameters.AddWithValue("@value", variable.ValueText.Trim());
        command.Parameters.AddWithValue("@type", string.IsNullOrWhiteSpace(variable.DataType) ? "text" : variable.DataType.Trim());
        command.Parameters.AddWithValue("@notes", variable.Notes.Trim());
        var id = Convert.ToInt64(command.ExecuteScalar());
        Log("SQL WRITE", $"CALL sp_save_user_variable id={id}");
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

    public IReadOnlyList<DatabaseActivityRecord> GetDatabaseActivity(int limit = 500)
    {
        Log("SQL READ", "SELECT database_activity_log ORDER BY changed_at DESC");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, changed_at, table_name, activity_type, row_id, summary
            FROM database_activity_log
            ORDER BY changed_at DESC, id DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var records = new List<DatabaseActivityRecord>();
        while (reader.Read())
        {
            records.Add(new DatabaseActivityRecord
            {
                Id = reader.GetInt64(0),
                ChangedAt = reader.GetDateTime(1).ToString("yyyy-MM-dd HH:mm:ss.fff"),
                TableName = reader.GetString(2),
                ActivityType = reader.GetString(3),
                RowId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                Summary = reader.GetString(5)
            });
        }

        return records;
    }

    public void ClearDatabaseActivity()
    {
        using var connection = OpenConnection();
        ExecuteNonQuery(connection, "DELETE FROM database_activity_log;", "DELETE database_activity_log");
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

    private void EnsureDatabaseRoutines(MySqlConnection connection)
    {
        // Die Trigger und Prozeduren sind bewusst Teil des MySQL-Schemas. Damit liegen
        // automatische Ableitungen und zentrale Schreibregeln nicht mehr im UI-Code.
        string[] routineDrops =
        [
            "DROP TRIGGER IF EXISTS trg_log_files_before_insert;",
            "DROP TRIGGER IF EXISTS trg_log_files_after_insert;",
            "DROP TRIGGER IF EXISTS trg_log_files_after_update;",
            "DROP TRIGGER IF EXISTS trg_log_files_after_delete;",
            "DROP TRIGGER IF EXISTS trg_mavlink_messages_before_insert;",
            "DROP TRIGGER IF EXISTS trg_mavlink_messages_after_insert;",
            "DROP TRIGGER IF EXISTS trg_mavlink_messages_after_delete;",
            "DROP TRIGGER IF EXISTS trg_mavlink_messages_after_update;",
            "DROP TRIGGER IF EXISTS trg_message_fields_before_insert;",
            "DROP TRIGGER IF EXISTS trg_message_fields_before_update;",
            "DROP TRIGGER IF EXISTS trg_message_fields_after_insert;",
            "DROP TRIGGER IF EXISTS trg_message_fields_after_update;",
            "DROP TRIGGER IF EXISTS trg_message_fields_after_delete;",
            "DROP TRIGGER IF EXISTS trg_user_variables_after_insert;",
            "DROP TRIGGER IF EXISTS trg_user_variables_after_update;",
            "DROP TRIGGER IF EXISTS trg_user_variables_after_delete;",
            "DROP PROCEDURE IF EXISTS sp_write_activity_log;",
            "DROP PROCEDURE IF EXISTS sp_create_log_file;",
            "DROP PROCEDURE IF EXISTS sp_ensure_message_type;",
            "DROP PROCEDURE IF EXISTS sp_insert_mavlink_message;",
            "DROP PROCEDURE IF EXISTS sp_insert_message_field;",
            "DROP PROCEDURE IF EXISTS sp_upsert_message_definition;",
            "DROP PROCEDURE IF EXISTS sp_delete_definition_fields;",
            "DROP PROCEDURE IF EXISTS sp_insert_field_definition;",
            "DROP PROCEDURE IF EXISTS sp_update_message_definition;",
            "DROP PROCEDURE IF EXISTS sp_update_field_definition;",
            "DROP PROCEDURE IF EXISTS sp_update_message_field;",
            "DROP PROCEDURE IF EXISTS sp_save_user_variable;"
        ];

        foreach (var sql in routineDrops)
        {
            ExecuteNonQuery(connection, sql, sql.TrimEnd(';'));
        }

        string[] routines =
        [
            """
            CREATE PROCEDURE sp_write_activity_log(
                IN p_table_name VARCHAR(64),
                IN p_activity_type VARCHAR(16),
                IN p_row_id BIGINT,
                IN p_summary TEXT
            )
            BEGIN
                INSERT INTO database_activity_log (table_name, activity_type, row_id, summary)
                VALUES (p_table_name, p_activity_type, p_row_id, COALESCE(p_summary, ''));
            END
            """,
            """
            CREATE TRIGGER trg_log_files_before_insert
            BEFORE INSERT ON log_files
            FOR EACH ROW
            BEGIN
                IF NEW.file_name IS NULL OR TRIM(NEW.file_name) = '' THEN
                    SET NEW.file_name = SUBSTRING_INDEX(REPLACE(NEW.path, '\\', '/'), '/', -1);
                END IF;

                IF NEW.imported_at IS NULL OR TRIM(NEW.imported_at) = '' THEN
                    SET NEW.imported_at = DATE_FORMAT(NOW(3), '%Y-%m-%d %H:%i:%s');
                END IF;

                IF NEW.message_count IS NULL OR NEW.message_count < 0 THEN
                    SET NEW.message_count = 0;
                END IF;
            END
            """,
            """
            CREATE TRIGGER trg_log_files_after_insert
            AFTER INSERT ON log_files
            FOR EACH ROW
            BEGIN
                CALL sp_write_activity_log(
                    'log_files',
                    'INSERT',
                    NEW.id,
                    CONCAT('Logdatei angelegt: ', NEW.file_name, ' (', LEFT(NEW.path, 500), ')')
                );
            END
            """,
            """
            CREATE TRIGGER trg_log_files_after_update
            AFTER UPDATE ON log_files
            FOR EACH ROW
            BEGIN
                IF NOT (
                    OLD.file_name <=> NEW.file_name AND
                    OLD.path <=> NEW.path AND
                    OLD.imported_at <=> NEW.imported_at AND
                    OLD.import_started_at <=> NEW.import_started_at
                ) THEN
                    CALL sp_write_activity_log(
                        'log_files',
                        'UPDATE',
                        NEW.id,
                        CONCAT('Logdatei geaendert: ', OLD.file_name, ' -> ', NEW.file_name)
                    );
                END IF;
            END
            """,
            """
            CREATE TRIGGER trg_log_files_after_delete
            AFTER DELETE ON log_files
            FOR EACH ROW
            BEGIN
                CALL sp_write_activity_log(
                    'log_files',
                    'DELETE',
                    OLD.id,
                    CONCAT('Logdatei geloescht: ', OLD.file_name, ' (', LEFT(OLD.path, 500), ')')
                );
            END
            """,
            """
            CREATE TRIGGER trg_mavlink_messages_before_insert
            BEFORE INSERT ON mavlink_messages
            FOR EACH ROW
            BEGIN
                IF NEW.route IS NULL OR TRIM(NEW.route) = '' THEN
                    SET NEW.route = CASE NEW.system_id
                        WHEN 100 THEN 'OpenHD Ground'
                        WHEN 101 THEN 'OpenHD Air'
                        WHEN 255 THEN 'QOpenHD'
                        WHEN 1 THEN 'Flight Controller'
                        WHEN 0 THEN 'Flight Controller'
                        ELSE CONCAT('System ', NEW.system_id)
                    END;
                END IF;

                IF NEW.packet_time_ms IS NULL OR NEW.packet_time_ms = 0 THEN
                    SET NEW.packet_time_ms = NEW.packet_index;
                END IF;

                IF NEW.packet_timestamp IS NULL OR TRIM(NEW.packet_timestamp) = '' THEN
                    SET NEW.packet_timestamp = (
                        SELECT DATE_FORMAT(DATE_ADD(import_started_at, INTERVAL (NEW.packet_index * 1000) MICROSECOND), '%Y-%m-%d %H:%i:%s.%f')
                        FROM log_files
                        WHERE id = NEW.log_file_id
                    );
                END IF;
            END
            """,
            """
            CREATE TRIGGER trg_mavlink_messages_after_insert
            AFTER INSERT ON mavlink_messages
            FOR EACH ROW
            BEGIN
                UPDATE log_files
                SET message_count = (
                    SELECT COUNT(*)
                    FROM mavlink_messages
                    WHERE log_file_id = NEW.log_file_id
                )
                WHERE id = NEW.log_file_id;

                CALL sp_write_activity_log(
                    'mavlink_messages',
                    'INSERT',
                    NEW.id,
                    CONCAT('MAVLink-Nachricht angelegt: log_file_id=', NEW.log_file_id, ', packet_index=', NEW.packet_index, ', system_id=', NEW.system_id)
                );
            END
            """,
            """
            CREATE TRIGGER trg_mavlink_messages_after_delete
            AFTER DELETE ON mavlink_messages
            FOR EACH ROW
            BEGIN
                UPDATE log_files
                SET message_count = (
                    SELECT COUNT(*)
                    FROM mavlink_messages
                    WHERE log_file_id = OLD.log_file_id
                )
                WHERE id = OLD.log_file_id;

                CALL sp_write_activity_log(
                    'mavlink_messages',
                    'DELETE',
                    OLD.id,
                    CONCAT('MAVLink-Nachricht geloescht: log_file_id=', OLD.log_file_id, ', packet_index=', OLD.packet_index, ', system_id=', OLD.system_id)
                );
            END
            """,
            """
            CREATE TRIGGER trg_mavlink_messages_after_update
            AFTER UPDATE ON mavlink_messages
            FOR EACH ROW
            BEGIN
                IF OLD.log_file_id <> NEW.log_file_id THEN
                    UPDATE log_files
                    SET message_count = (
                        SELECT COUNT(*)
                        FROM mavlink_messages
                        WHERE log_file_id = OLD.log_file_id
                    )
                    WHERE id = OLD.log_file_id;

                    UPDATE log_files
                    SET message_count = (
                        SELECT COUNT(*)
                        FROM mavlink_messages
                        WHERE log_file_id = NEW.log_file_id
                    )
                    WHERE id = NEW.log_file_id;
                END IF;

                CALL sp_write_activity_log(
                    'mavlink_messages',
                    'UPDATE',
                    NEW.id,
                    CONCAT('MAVLink-Nachricht geaendert: log_file_id ', OLD.log_file_id, ' -> ', NEW.log_file_id, ', packet_index=', NEW.packet_index)
                );
            END
            """,
            """
            CREATE TRIGGER trg_message_fields_before_insert
            BEFORE INSERT ON message_fields
            FOR EACH ROW
            BEGIN
                SET NEW.field_name = TRIM(NEW.field_name);
                SET NEW.value_text = TRIM(NEW.value_text);
                SET NEW.unit = TRIM(COALESCE(NEW.unit, ''));

                IF NEW.numeric_value IS NULL AND NEW.value_text REGEXP '^-?[0-9]+(\\.[0-9]+)?$' THEN
                    SET NEW.numeric_value = CAST(NEW.value_text AS DECIMAL(30,10));
                END IF;
            END
            """,
            """
            CREATE TRIGGER trg_message_fields_after_insert
            AFTER INSERT ON message_fields
            FOR EACH ROW
            BEGIN
                CALL sp_write_activity_log(
                    'message_fields',
                    'INSERT',
                    NEW.id,
                    CONCAT('Feld angelegt: message_id=', NEW.message_id, ', ', NEW.field_name, '=', LEFT(NEW.value_text, 500))
                );
            END
            """,
            """
            CREATE TRIGGER trg_message_fields_after_update
            AFTER UPDATE ON message_fields
            FOR EACH ROW
            BEGIN
                CALL sp_write_activity_log(
                    'message_fields',
                    'UPDATE',
                    NEW.id,
                    CONCAT('Feld geaendert: ', OLD.field_name, '=', LEFT(OLD.value_text, 250), ' -> ', NEW.field_name, '=', LEFT(NEW.value_text, 250))
                );
            END
            """,
            """
            CREATE TRIGGER trg_message_fields_after_delete
            AFTER DELETE ON message_fields
            FOR EACH ROW
            BEGIN
                CALL sp_write_activity_log(
                    'message_fields',
                    'DELETE',
                    OLD.id,
                    CONCAT('Feld geloescht: message_id=', OLD.message_id, ', ', OLD.field_name, '=', LEFT(OLD.value_text, 500))
                );
            END
            """,
            """
            CREATE TRIGGER trg_user_variables_after_insert
            AFTER INSERT ON user_variables
            FOR EACH ROW
            BEGIN
                CALL sp_write_activity_log(
                    'user_variables',
                    'INSERT',
                    NEW.id,
                    CONCAT('Variable angelegt: ', NEW.name, '=', LEFT(NEW.value_text, 500))
                );
            END
            """,
            """
            CREATE TRIGGER trg_user_variables_after_update
            AFTER UPDATE ON user_variables
            FOR EACH ROW
            BEGIN
                CALL sp_write_activity_log(
                    'user_variables',
                    'UPDATE',
                    NEW.id,
                    CONCAT('Variable geaendert: ', OLD.name, '=', LEFT(OLD.value_text, 250), ' -> ', NEW.name, '=', LEFT(NEW.value_text, 250))
                );
            END
            """,
            """
            CREATE TRIGGER trg_user_variables_after_delete
            AFTER DELETE ON user_variables
            FOR EACH ROW
            BEGIN
                CALL sp_write_activity_log(
                    'user_variables',
                    'DELETE',
                    OLD.id,
                    CONCAT('Variable geloescht: ', OLD.name, '=', LEFT(OLD.value_text, 500))
                );
            END
            """,
            """
            CREATE TRIGGER trg_message_fields_before_update
            BEFORE UPDATE ON message_fields
            FOR EACH ROW
            BEGIN
                SET NEW.field_name = TRIM(NEW.field_name);
                SET NEW.value_text = TRIM(NEW.value_text);
                SET NEW.unit = TRIM(COALESCE(NEW.unit, ''));

                IF NEW.numeric_value IS NULL AND NEW.value_text REGEXP '^-?[0-9]+(\\.[0-9]+)?$' THEN
                    SET NEW.numeric_value = CAST(NEW.value_text AS DECIMAL(30,10));
                END IF;
            END
            """,
            """
            CREATE PROCEDURE sp_create_log_file(
                IN p_path TEXT,
                IN p_imported_at VARCHAR(64)
            )
            BEGIN
                INSERT INTO log_files (file_name, path, imported_at, message_count)
                VALUES ('', p_path, p_imported_at, 0);

                SELECT LAST_INSERT_ID() AS id;
            END
            """,
            """
            CREATE PROCEDURE sp_ensure_message_type(
                IN p_message_id INT,
                IN p_name VARCHAR(255),
                IN p_dialect VARCHAR(255)
            )
            BEGIN
                INSERT INTO message_types (message_id, name, dialect)
                VALUES (p_message_id, TRIM(p_name), TRIM(COALESCE(p_dialect, '')))
                ON DUPLICATE KEY UPDATE
                    id = LAST_INSERT_ID(id),
                    name = VALUES(name),
                    dialect = VALUES(dialect);

                SELECT LAST_INSERT_ID() AS id;
            END
            """,
            """
            CREATE PROCEDURE sp_insert_mavlink_message(
                IN p_log_file_id BIGINT,
                IN p_message_type_id BIGINT,
                IN p_packet_index INT,
                IN p_packet_time_ms BIGINT,
                IN p_packet_timestamp VARCHAR(64),
                IN p_byte_offset INT,
                IN p_mavlink_version INT,
                IN p_sequence INT,
                IN p_system_id INT,
                IN p_component_id INT,
                IN p_payload_length INT,
                IN p_checksum VARCHAR(32),
                IN p_raw_packet_hex LONGTEXT
            )
            BEGIN
                INSERT INTO mavlink_messages
                    (log_file_id, message_type_id, packet_index, packet_time_ms, packet_timestamp,
                     byte_offset, mavlink_version, sequence, system_id, component_id,
                     route, payload_length, checksum, raw_packet_hex)
                VALUES
                    (p_log_file_id, p_message_type_id, p_packet_index, COALESCE(p_packet_time_ms, 0), COALESCE(p_packet_timestamp, ''),
                     p_byte_offset, p_mavlink_version, p_sequence, p_system_id, p_component_id,
                     '', p_payload_length, p_checksum, p_raw_packet_hex);

                SELECT LAST_INSERT_ID() AS id;
            END
            """,
            """
            CREATE PROCEDURE sp_insert_message_field(
                IN p_message_id BIGINT,
                IN p_field_name VARCHAR(255),
                IN p_value_text TEXT,
                IN p_numeric_value DOUBLE,
                IN p_unit VARCHAR(64)
            )
            BEGIN
                INSERT INTO message_fields (message_id, field_name, value_text, numeric_value, unit)
                VALUES (p_message_id, p_field_name, p_value_text, p_numeric_value, COALESCE(p_unit, ''));
            END
            """,
            """
            CREATE PROCEDURE sp_upsert_message_definition(
                IN p_message_id INT,
                IN p_name VARCHAR(255),
                IN p_dialect VARCHAR(255),
                IN p_payload_length INT,
                IN p_crc_extra INT,
                IN p_source_file VARCHAR(1024),
                IN p_notes TEXT
            )
            BEGIN
                INSERT INTO message_definitions
                    (message_id, name, dialect, payload_length, crc_extra, source_file, notes)
                VALUES
                    (p_message_id, TRIM(p_name), TRIM(p_dialect), p_payload_length, p_crc_extra, TRIM(p_source_file), COALESCE(p_notes, ''))
                ON DUPLICATE KEY UPDATE
                    id = LAST_INSERT_ID(id),
                    name = VALUES(name),
                    dialect = VALUES(dialect),
                    payload_length = VALUES(payload_length),
                    crc_extra = VALUES(crc_extra),
                    source_file = VALUES(source_file),
                    notes = VALUES(notes);

                SELECT LAST_INSERT_ID() AS id;
            END
            """,
            """
            CREATE PROCEDURE sp_delete_definition_fields(IN p_definition_id BIGINT)
            BEGIN
                DELETE FROM field_definitions WHERE definition_id = p_definition_id;
            END
            """,
            """
            CREATE PROCEDURE sp_insert_field_definition(
                IN p_definition_id BIGINT,
                IN p_field_name VARCHAR(255),
                IN p_value_type VARCHAR(64),
                IN p_array_length INT,
                IN p_payload_offset INT,
                IN p_unit VARCHAR(64),
                IN p_description TEXT
            )
            BEGIN
                INSERT INTO field_definitions
                    (definition_id, field_name, value_type, array_length, payload_offset, unit, description)
                VALUES
                    (p_definition_id, TRIM(p_field_name), TRIM(p_value_type), p_array_length, p_payload_offset, TRIM(COALESCE(p_unit, '')), COALESCE(p_description, ''));
            END
            """,
            """
            CREATE PROCEDURE sp_update_message_definition(
                IN p_id BIGINT,
                IN p_message_id INT,
                IN p_name VARCHAR(255),
                IN p_dialect VARCHAR(255),
                IN p_payload_length INT,
                IN p_crc_extra INT,
                IN p_source_file VARCHAR(1024),
                IN p_notes TEXT
            )
            BEGIN
                UPDATE message_definitions
                SET message_id = p_message_id,
                    name = TRIM(p_name),
                    dialect = TRIM(p_dialect),
                    payload_length = p_payload_length,
                    crc_extra = p_crc_extra,
                    source_file = TRIM(p_source_file),
                    notes = COALESCE(p_notes, '')
                WHERE id = p_id;
            END
            """,
            """
            CREATE PROCEDURE sp_update_field_definition(
                IN p_id BIGINT,
                IN p_field_name VARCHAR(255),
                IN p_value_type VARCHAR(64),
                IN p_array_length INT,
                IN p_payload_offset INT,
                IN p_unit VARCHAR(64),
                IN p_description TEXT
            )
            BEGIN
                UPDATE field_definitions
                SET field_name = TRIM(p_field_name),
                    value_type = TRIM(p_value_type),
                    array_length = p_array_length,
                    payload_offset = p_payload_offset,
                    unit = TRIM(COALESCE(p_unit, '')),
                    description = COALESCE(p_description, '')
                WHERE id = p_id;
            END
            """,
            """
            CREATE PROCEDURE sp_update_message_field(
                IN p_id BIGINT,
                IN p_field_name VARCHAR(255),
                IN p_value_text TEXT,
                IN p_numeric_value DOUBLE,
                IN p_unit VARCHAR(64)
            )
            BEGIN
                UPDATE message_fields
                SET field_name = p_field_name,
                    value_text = p_value_text,
                    numeric_value = p_numeric_value,
                    unit = COALESCE(p_unit, '')
                WHERE id = p_id;
            END
            """,
            """
            CREATE PROCEDURE sp_save_user_variable(
                IN p_id BIGINT,
                IN p_name VARCHAR(255),
                IN p_value_text TEXT,
                IN p_data_type VARCHAR(64),
                IN p_notes TEXT
            )
            BEGIN
                IF p_id = 0 THEN
                    INSERT INTO user_variables (name, value_text, data_type, notes)
                    VALUES (TRIM(p_name), TRIM(p_value_text), COALESCE(NULLIF(TRIM(p_data_type), ''), 'text'), TRIM(COALESCE(p_notes, '')));

                    SELECT LAST_INSERT_ID() AS id;
                ELSE
                    UPDATE user_variables
                    SET name = TRIM(p_name),
                        value_text = TRIM(p_value_text),
                        data_type = COALESCE(NULLIF(TRIM(p_data_type), ''), 'text'),
                        notes = TRIM(COALESCE(p_notes, ''))
                    WHERE id = p_id;

                    SELECT p_id AS id;
                END IF;
            END
            """
        ];

        foreach (var sql in routines)
        {
            ExecuteNonQuery(connection, sql, "CREATE MySQL trigger/procedure");
        }
    }

    private long InsertLogFile(MySqlConnection connection, MySqlTransaction transaction, string path, DateTimeOffset importedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CALL sp_create_log_file(@path, @importedAt);";
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@importedAt", importedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long EnsureMessageType(MySqlConnection connection, MySqlTransaction transaction, int messageId, string name, string dialect)
    {
        // message_types normalisiert Message-ID, Name und Dialekt. Importierte
        // mavlink_messages referenzieren diese Tabelle, damit Name/Dialekt nicht in
        // jeder Nachricht wiederholt werden muessen.
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "CALL sp_ensure_message_type(@messageId, @name, @dialect);";
        insert.Parameters.AddWithValue("@messageId", messageId);
        insert.Parameters.AddWithValue("@name", name);
        insert.Parameters.AddWithValue("@dialect", dialect);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    private static long InsertMessage(MySqlConnection connection, MySqlTransaction transaction, long logId, long messageTypeId, ImportedMavlinkMessage message)
    {
        // Wenn eine Sidecar-Zeitinformation vorhanden ist, wird sie bevorzugt. Sonst
        // setzt der MySQL-Trigger Paketindex und Importzeit als Ersatzzeit.
        var frame = message.Frame;
        var timing = message.Timing;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CALL sp_insert_mavlink_message(
                @logId, @messageTypeId, @packetIndex, @packetTimeMs, @packetTimestamp,
                @byteOffset, @version, @sequence, @systemId, @componentId,
                @payloadLength, @checksum, @rawPacketHex);
            """;
        command.Parameters.AddWithValue("@logId", logId);
        command.Parameters.AddWithValue("@messageTypeId", messageTypeId);
        command.Parameters.AddWithValue("@packetIndex", frame.PacketIndex);
        command.Parameters.AddWithValue("@packetTimeMs", (object?)timing?.ElapsedMs ?? DBNull.Value);
        command.Parameters.AddWithValue("@packetTimestamp", string.IsNullOrWhiteSpace(timing?.Timestamp) ? DBNull.Value : timing.Timestamp);
        command.Parameters.AddWithValue("@byteOffset", frame.ByteOffset);
        command.Parameters.AddWithValue("@version", frame.Version);
        command.Parameters.AddWithValue("@sequence", frame.Sequence);
        command.Parameters.AddWithValue("@systemId", frame.SystemId);
        command.Parameters.AddWithValue("@componentId", frame.ComponentId);
        command.Parameters.AddWithValue("@payloadLength", frame.Payload.Length);
        command.Parameters.AddWithValue("@checksum", $"0x{frame.Checksum:X4}");
        command.Parameters.AddWithValue("@rawPacketHex", Convert.ToHexString(frame.RawPacket));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void InsertField(MySqlConnection connection, MySqlTransaction transaction, long messageId, DecodedField field)
    {
        // numeric_value ist nullable: Textfelder und Arrays bleiben nur als value_text
        // erhalten, echte Zahlen koennen zusaetzlich sortiert/gefiltert werden.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CALL sp_insert_message_field(@messageId, @name, @value, @numericValue, @unit);";
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
        command.CommandText = "CALL sp_upsert_message_definition(@messageId, @name, @dialect, @payloadLength, @crcExtra, @sourceFile, @notes);";
        command.Parameters.AddWithValue("@messageId", definition.MessageId);
        command.Parameters.AddWithValue("@name", definition.Name);
        command.Parameters.AddWithValue("@dialect", definition.Dialect);
        command.Parameters.AddWithValue("@payloadLength", definition.PayloadLength);
        command.Parameters.AddWithValue("@crcExtra", definition.CrcExtra);
        command.Parameters.AddWithValue("@sourceFile", definition.SourceFile);
        command.Parameters.AddWithValue("@notes", definition.Notes);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void DeleteDefinitionFields(MySqlConnection connection, MySqlTransaction transaction, long definitionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CALL sp_delete_definition_fields(@definitionId);";
        command.Parameters.AddWithValue("@definitionId", definitionId);
        command.ExecuteNonQuery();
    }

    private static void InsertFieldDefinition(MySqlConnection connection, MySqlTransaction transaction, long definitionId, MavlinkFieldDefinitionRecord field)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CALL sp_insert_field_definition(@definitionId, @fieldName, @valueType, @arrayLength, @payloadOffset, @unit, @description);";
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

