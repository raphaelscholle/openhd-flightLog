const fs = require('fs');
const path = require('path');

const outPath = path.join(__dirname, 'sql-triggers-overview.pdf');

function esc(text) {
  return String(text)
    .replace(/\\/g, '\\\\')
    .replace(/\(/g, '\\(')
    .replace(/\)/g, '\\)')
    .replace(/\r?\n/g, ' ');
}

function wrap(text, max) {
  const words = String(text).split(/\s+/);
  const lines = [];
  let line = '';
  for (const word of words) {
    if (!line) {
      line = word;
    } else if ((line + ' ' + word).length <= max) {
      line += ' ' + word;
    } else {
      lines.push(line);
      line = word;
    }
  }
  if (line) lines.push(line);
  return lines;
}

class Pdf {
  constructor() {
    this.objects = [];
    this.pages = [];
    this.fonts = null;
  }

  addObject(body) {
    this.objects.push(body);
    return this.objects.length;
  }

  cmd(page, text) {
    page.commands.push(text);
  }

  color(page, stroke, fill) {
    if (stroke) this.cmd(page, `${stroke.join(' ')} RG`);
    if (fill) this.cmd(page, `${fill.join(' ')} rg`);
  }

  line(page, x1, y1, x2, y2) {
    this.cmd(page, `${x1} ${y1} m ${x2} ${y2} l S`);
  }

  rect(page, x, y, w, h, fill = [1, 1, 1], stroke = [0.18, 0.22, 0.27]) {
    this.color(page, stroke, fill);
    this.cmd(page, `${x} ${y} ${w} ${h} re B`);
  }

  text(page, x, y, text, size = 10, font = 'F1', fill = [0.08, 0.1, 0.13]) {
    this.color(page, null, fill);
    this.cmd(page, `BT /${font} ${size} Tf ${x} ${y} Td (${esc(text)}) Tj ET`);
  }

  lines(page, x, y, lines, size = 9, font = 'F1', leading = 12, fill = [0.08, 0.1, 0.13]) {
    lines.forEach((line, i) => this.text(page, x, y - i * leading, line, size, font, fill));
  }

  arrow(page, x1, y1, x2, y2) {
    this.color(page, [0.2, 0.28, 0.36], null);
    this.cmd(page, `1.3 w`);
    this.line(page, x1, y1, x2, y2);
    const a = Math.atan2(y2 - y1, x2 - x1);
    const len = 7;
    const p1 = [x2 - len * Math.cos(a - Math.PI / 6), y2 - len * Math.sin(a - Math.PI / 6)];
    const p2 = [x2 - len * Math.cos(a + Math.PI / 6), y2 - len * Math.sin(a + Math.PI / 6)];
    this.cmd(page, `${x2} ${y2} m ${p1[0].toFixed(2)} ${p1[1].toFixed(2)} l ${p2[0].toFixed(2)} ${p2[1].toFixed(2)} l h f`);
    this.cmd(page, `1 w`);
  }

  box(page, x, y, w, h, title, body, fill = [0.94, 0.97, 1]) {
    this.rect(page, x, y, w, h, fill, [0.16, 0.28, 0.42]);
    this.text(page, x + 10, y + h - 18, title, 11, 'F1', [0.04, 0.16, 0.28]);
    this.lines(page, x + 10, y + h - 34, wrap(body, Math.floor(w / 5.2)), 8.5, 'F1', 10.5, [0.18, 0.21, 0.25]);
  }

  codeBox(page, x, y, w, h, title, code) {
    this.rect(page, x, y, w, h, [0.98, 0.98, 0.96], [0.35, 0.35, 0.31]);
    this.text(page, x + 10, y + h - 17, title, 10.5, 'F1', [0.18, 0.18, 0.16]);
    const maxChars = Math.floor((w - 20) / 4.7);
    const lines = [];
    for (const raw of code.trim().split(/\r?\n/)) {
      if (raw.length <= maxChars) {
        lines.push(raw);
      } else {
        for (let i = 0; i < raw.length; i += maxChars) lines.push(raw.slice(i, i + maxChars));
      }
    }
    this.lines(page, x + 10, y + h - 34, lines.slice(0, Math.floor((h - 42) / 9.5)), 7.8, 'F2', 9.5, [0.12, 0.12, 0.12]);
  }

  createPage(width = 842, height = 595) {
    const page = { width, height, commands: [] };
    this.pages.push(page);
    return page;
  }

  build() {
    const fontHelvetica = this.addObject('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>');
    const fontCourier = this.addObject('<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>');
    const pageObjs = [];
    for (const page of this.pages) {
      const stream = page.commands.join('\n');
      const contentObj = this.addObject(`<< /Length ${Buffer.byteLength(stream)} >>\nstream\n${stream}\nendstream`);
      const pageObj = this.addObject(`<< /Type /Page /Parent PAGES_REF /MediaBox [0 0 ${page.width} ${page.height}] /Resources << /Font << /F1 ${fontHelvetica} 0 R /F2 ${fontCourier} 0 R >> >> /Contents ${contentObj} 0 R >>`);
      pageObjs.push(pageObj);
    }
    const pagesObj = this.addObject(`<< /Type /Pages /Kids [${pageObjs.map(id => `${id} 0 R`).join(' ')}] /Count ${pageObjs.length} >>`);
    for (const id of pageObjs) {
      this.objects[id - 1] = this.objects[id - 1].replace('PAGES_REF', `${pagesObj} 0 R`);
    }
    const catalogObj = this.addObject(`<< /Type /Catalog /Pages ${pagesObj} 0 R >>`);

    let pdf = '%PDF-1.4\n';
    const offsets = [0];
    this.objects.forEach((body, i) => {
      offsets.push(Buffer.byteLength(pdf));
      pdf += `${i + 1} 0 obj\n${body}\nendobj\n`;
    });
    const xref = Buffer.byteLength(pdf);
    pdf += `xref\n0 ${this.objects.length + 1}\n0000000000 65535 f \n`;
    for (let i = 1; i < offsets.length; i++) {
      pdf += `${String(offsets[i]).padStart(10, '0')} 00000 n \n`;
    }
    pdf += `trailer\n<< /Size ${this.objects.length + 1} /Root ${catalogObj} 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
    return Buffer.from(pdf, 'binary');
  }
}

const pdf = new Pdf();

const p1 = pdf.createPage();
pdf.text(p1, 42, 555, 'OpenHD FlightLog SQL Trigger Logging', 24, 'F1', [0.04, 0.12, 0.2]);
pdf.text(p1, 44, 532, 'How triggers and stored procedures track database activity automatically', 11, 'F1', [0.34, 0.38, 0.43]);

pdf.box(p1, 42, 445, 145, 54, 'App starts', 'FlightLogDatabase constructor opens MariaDB and calls EnsureDatabase() and EnsureSchema().', [0.93, 0.97, 1]);
pdf.box(p1, 230, 445, 155, 54, 'Schema setup', 'EnsureSchema() creates tables, indexes, migrations, then calls EnsureDatabaseRoutines().', [0.94, 0.98, 0.94]);
pdf.box(p1, 430, 445, 165, 54, 'Routines recreated', 'Old triggers/procedures are dropped, then sp_write_activity_log and all CREATE TRIGGER statements are executed.', [1, 0.97, 0.91]);
pdf.box(p1, 645, 445, 150, 54, 'Activity log', 'Every tracked INSERT, UPDATE, and DELETE writes a row into database_activity_log.', [0.97, 0.95, 1]);
pdf.arrow(p1, 187, 472, 230, 472);
pdf.arrow(p1, 385, 472, 430, 472);
pdf.arrow(p1, 595, 472, 645, 472);

pdf.text(p1, 42, 398, 'Import path: C# calls stored procedures; trigger execution is automatic.', 13, 'F1', [0.04, 0.12, 0.2]);
pdf.box(p1, 54, 320, 145, 48, 'SaveImportedLog()', 'Loops imported MAVLink messages and fields inside one transaction.', [0.95, 0.98, 1]);
pdf.box(p1, 245, 345, 185, 44, 'CALL sp_create_log_file', 'Inserts into log_files.', [0.97, 0.98, 0.94]);
pdf.box(p1, 245, 280, 185, 48, 'CALL sp_insert_mavlink_message', 'Inserts into mavlink_messages.', [0.97, 0.98, 0.94]);
pdf.box(p1, 245, 215, 185, 48, 'CALL sp_insert_message_field', 'Inserts into message_fields.', [0.97, 0.98, 0.94]);
pdf.box(p1, 500, 345, 260, 44, 'log_files triggers', 'Normalize the row and log INSERT/UPDATE/DELETE activity.', [1, 0.96, 0.91]);
pdf.box(p1, 500, 280, 260, 48, 'mavlink triggers', 'Derive route/time, update message_count, and log message inserts/deletes/updates.', [1, 0.96, 0.91]);
pdf.box(p1, 500, 215, 260, 48, 'message_fields triggers', 'Trim/parse values and log field inserts/updates/deletes.', [1, 0.96, 0.91]);
pdf.arrow(p1, 199, 344, 245, 367);
pdf.arrow(p1, 199, 344, 245, 304);
pdf.arrow(p1, 199, 344, 245, 239);
pdf.arrow(p1, 430, 367, 500, 367);
pdf.arrow(p1, 430, 304, 500, 304);
pdf.arrow(p1, 430, 239, 500, 239);

pdf.text(p1, 42, 164, 'Edit path: field edits also pass through database rules.', 13, 'F1', [0.04, 0.12, 0.2]);
pdf.box(p1, 54, 92, 170, 44, 'UI edits field', 'Database.SaveField() is called.', [0.95, 0.98, 1]);
pdf.box(p1, 276, 92, 205, 44, 'CALL sp_update_message_field', 'Updates message_fields row.', [0.97, 0.98, 0.94]);
pdf.box(p1, 540, 92, 220, 44, 'update trigger + activity row', 'Normalizes the update and writes the change into database_activity_log.', [1, 0.96, 0.91]);
pdf.arrow(p1, 224, 114, 276, 114);
pdf.arrow(p1, 481, 114, 540, 114);

const p2 = pdf.createPage();
pdf.text(p2, 42, 555, 'Implementation Snippets', 22, 'F1', [0.04, 0.12, 0.2]);
pdf.text(p2, 44, 533, 'Source: OpenHdFlightLog/Services/FlightLogDatabase.cs', 10.5, 'F1', [0.34, 0.38, 0.43]);

pdf.codeBox(p2, 42, 350, 360, 150, '1. Activity log table and writer procedure', `
CREATE TABLE IF NOT EXISTS database_activity_log (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    changed_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    table_name VARCHAR(64) NOT NULL,
    activity_type VARCHAR(16) NOT NULL,
    row_id BIGINT NULL,
    summary TEXT NOT NULL
);

CREATE PROCEDURE sp_write_activity_log(...)
BEGIN
    INSERT INTO database_activity_log
        (table_name, activity_type, row_id, summary)
    VALUES (...);
END`);

pdf.codeBox(p2, 432, 350, 365, 150, '2. Routines are recreated during schema setup', `
private void EnsureDatabaseRoutines(MySqlConnection connection)
{
    string[] routineDrops =
    [
        "DROP TRIGGER IF EXISTS trg_log_files_before_insert;",
        "DROP TRIGGER IF EXISTS trg_log_files_after_insert;",
        "DROP PROCEDURE IF EXISTS sp_write_activity_log;"
    ];

    foreach (var sql in routineDrops)
        ExecuteNonQuery(connection, sql, sql.TrimEnd(';'));

    string[] routines = [ "CREATE TRIGGER ...", "CREATE PROCEDURE ..." ];
}`);

pdf.codeBox(p2, 42, 175, 360, 145, '3. C# calls procedures, triggers fire automatically', `
command.CommandText =
    "CALL sp_insert_mavlink_message(...);";

-- inside the procedure
INSERT INTO mavlink_messages (...)
VALUES (...);

-- then MySQL runs the trigger
CREATE TRIGGER trg_mavlink_messages_after_insert
AFTER INSERT ON mavlink_messages
FOR EACH ROW
BEGIN
    CALL sp_write_activity_log(
        'mavlink_messages', 'INSERT', NEW.id,
        CONCAT('packet_index=', NEW.packet_index));
END`);

pdf.codeBox(p2, 432, 175, 365, 145, '4. Edit tracking trigger example', `
CREATE TRIGGER trg_user_variables_after_update
AFTER UPDATE ON user_variables
FOR EACH ROW
BEGIN
    CALL sp_write_activity_log(
        'user_variables',
        'UPDATE',
        NEW.id,
        CONCAT('Variable geaendert: ',
               OLD.name, ' -> ', NEW.name));
END`);

pdf.rect(p2, 42, 48, 755, 92, [0.94, 0.97, 1], [0.18, 0.28, 0.42]);
pdf.text(p2, 58, 118, 'Why we need them', 15, 'F1', [0.04, 0.12, 0.2]);
pdf.lines(p2, 58, 99, [
  '- Fulfills the assignment requirement for a persistent activity log table.',
  '- Tracks inserts, updates, and deletes automatically in MariaDB, not manually in UI code.',
  '- Uses stored procedures for write paths and a stored procedure for trigger logging.',
  '- Shows the protocol in the DB Activity tab: table, operation, row id, time, summary.',
  '- Keeps the older normalization triggers for route, timestamps, counts, and field cleanup.'
], 9.5, 'F1', 12, [0.14, 0.17, 0.2]);

const p3 = pdf.createPage();
pdf.text(p3, 42, 555, 'Old vs New Database Activity Tracking', 22, 'F1', [0.04, 0.12, 0.2]);
pdf.text(p3, 44, 533, 'What changed compared to a non-trigger or UI-only logging approach', 10.5, 'F1', [0.34, 0.38, 0.43]);

pdf.rect(p3, 42, 455, 365, 52, [1, 0.96, 0.94], [0.45, 0.24, 0.18]);
pdf.text(p3, 58, 487, 'Older / non-trigger approach', 15, 'F1', [0.28, 0.08, 0.04]);
pdf.lines(p3, 58, 468, [
  'Application code writes data and optionally writes debug text.',
  'Tracking depends on every C# write path remembering to log manually.'
], 9, 'F1', 11, [0.22, 0.13, 0.1]);

pdf.rect(p3, 435, 455, 365, 52, [0.93, 0.98, 0.95], [0.16, 0.38, 0.22]);
pdf.text(p3, 451, 487, 'New trigger-based approach', 15, 'F1', [0.04, 0.23, 0.1]);
pdf.lines(p3, 451, 468, [
  'Stored procedures perform writes; triggers automatically log the result.',
  'Tracking lives in MariaDB and is persisted in database_activity_log.'
], 9, 'F1', 11, [0.1, 0.2, 0.13]);

pdf.codeBox(p3, 42, 275, 365, 145, 'Before: manual or missing tracking in C#', `
public void DeleteVariable(long variableId)
{
    command.CommandText =
        "DELETE FROM user_variables WHERE id = @id;";
    command.ExecuteNonQuery();

    // Only UI/debug output. If another code path deletes
    // data and forgets this line, no persistent audit row exists.
    Log("SQL WRITE", $"DELETE user_variables id={variableId}");
}`);

pdf.codeBox(p3, 435, 275, 365, 145, 'After: database-level tracking', `
CREATE TRIGGER trg_user_variables_after_delete
AFTER DELETE ON user_variables
FOR EACH ROW
BEGIN
    CALL sp_write_activity_log(
        'user_variables',
        'DELETE',
        OLD.id,
        CONCAT('Variable geloescht: ', OLD.name));
END`);

pdf.text(p3, 42, 238, 'Practical differences', 15, 'F1', [0.04, 0.12, 0.2]);
pdf.rect(p3, 42, 74, 755, 145, [0.97, 0.98, 0.99], [0.24, 0.29, 0.34]);
pdf.text(p3, 58, 197, 'Without trigger logging', 12, 'F1', [0.22, 0.13, 0.1]);
pdf.lines(p3, 58, 178, [
  '- Logs are application behavior, not database behavior.',
  '- Direct SQL changes are invisible unless the caller logs manually.',
  '- A missed Log(...) call means the change is not traceable.',
  '- DebugEvents disappear when the app restarts.',
  '- Does not clearly satisfy the assignment requirement for a log table.'
], 9, 'F1', 11.5, [0.18, 0.16, 0.14]);

pdf.text(p3, 438, 197, 'With trigger logging', 12, 'F1', [0.04, 0.23, 0.1]);
pdf.lines(p3, 438, 178, [
  '- Logs are enforced by MariaDB whenever tracked rows change.',
  '- Insert, update, and delete activity is written automatically.',
  '- database_activity_log remains available after restart.',
  '- The DB Activity tab can show an audit trail from the table.',
  '- Matches: Log table + triggers + stored procedures.'
], 9, 'F1', 11.5, [0.12, 0.18, 0.14]);

fs.writeFileSync(outPath, pdf.build());
console.log(outPath);
