<?php
declare(strict_types=1);

header('Content-Type: text/plain; charset=utf-8');

function respond(string $text): void
{
    echo $text;
    exit;
}

function parameter(int $number): string
{
    $key = sprintf('p%02d', $number);
    return trim((string)($_GET[$key] ?? ''));
}

function helpText(): string
{
    return implode("\n", [
        'Befehle:',
        'register NAME',
        'deregister',
        'users',
        'send EMPFÄNGER NACHRICHT',
        'read',
        'status',
        'help',
    ]);
}

function getNodeAddress(): string
{
    // Meshtastic-Node-ID bevorzugen.
    $id = trim((string)($_GET['id'] ?? ''));

    if ($id !== '') {
        return strtolower($id);
    }

    // Numerische Node-Adresse als Alternative.
    $number = trim((string)($_GET['number'] ?? ''));

    if ($number !== '' && ctype_digit($number)) {
        return $number;
    }

    respond('Fehler: Node-Adresse fehlt.');
}

function findCurrentUser(PDO $db, string $nodeAddress): ?array
{
    $statement = $db->prepare(
        'SELECT id, name
         FROM users
         WHERE node_address = ?'
    );

    $statement->execute([$nodeAddress]);
    $user = $statement->fetch(PDO::FETCH_ASSOC);

    return $user === false ? null : $user;
}

function textLength(string $text): int
{
    if (function_exists('mb_strlen')) {
        return mb_strlen($text, 'UTF-8');
    }

    $result = preg_match_all('/./us', $text, $matches);

    if ($result === false) {
        return strlen($text);
    }

    return $result;
}

try {
    date_default_timezone_set(
        getenv('MAILBOT_TIMEZONE') ?: 'Europe/Berlin'
    );

    /*
     * Optional kann über MAILBOT_DB_PATH ein anderer Speicherort
     * festgelegt werden.
     */
    $databasePath = getenv('MAILBOT_DB_PATH');

    if (!$databasePath) {
        $dataDirectory = __DIR__ . DIRECTORY_SEPARATOR . 'data';

        if (
            !is_dir($dataDirectory)
            && !mkdir($dataDirectory, 0770, true)
            && !is_dir($dataDirectory)
        ) {
            throw new RuntimeException(
                'Das Datenverzeichnis konnte nicht erstellt werden.'
            );
        }

        $databasePath =
            $dataDirectory
            . DIRECTORY_SEPARATOR
            . 'mailbot.sqlite';
    }

    $db = new PDO(
        'sqlite:' . $databasePath,
        null,
        null,
        [
            PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
            PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
        ]
    );

    $db->exec('PRAGMA foreign_keys = ON');
    $db->exec('PRAGMA busy_timeout = 5000');

    $db->exec(
        'CREATE TABLE IF NOT EXISTS users (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            node_address TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL UNIQUE
        )'
    );

    $db->exec(
        'CREATE TABLE IF NOT EXISTS mails (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            recipient_id INTEGER NOT NULL,
            sender_name TEXT NOT NULL,
            message TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(recipient_id)
                REFERENCES users(id)
                ON DELETE CASCADE
        )'
    );

    /*
     * Der ConsoleClient verarbeitet den frei konfigurierbaren
     * Bot-Aufruf. Deshalb ist p01 direkt der eigentliche Befehl.
     *
     * Der Parameter "message" wird bewusst nicht ausgewertet.
     */
    $command = strtolower(parameter(1));

    if ($command === '' || $command === 'help') {
        respond(helpText());
    }

    $nodeAddress = getNodeAddress();
    $currentUser = findCurrentUser($db, $nodeAddress);

    switch ($command) {
        case 'register':
            $name = parameter(2);

            if (
                $name === ''
                || strlen($name) > 32
                || preg_match('/^[A-Za-z0-9-]+$/D', $name) !== 1
            ) {
                respond(
                    'Fehler: Der Name darf nur Buchstaben, Zahlen '
                    . 'und Bindestriche enthalten (maximal 32 Zeichen).'
                );
            }

            if ($currentUser !== null) {
                respond(
                    'Fehler: Diese Node ist bereits als '
                    . $currentUser['name']
                    . ' registriert.'
                );
            }

            try {
                $statement = $db->prepare(
                    'INSERT INTO users (
                        node_address,
                        name,
                        name_key
                    ) VALUES (?, ?, ?)'
                );

                $statement->execute([
                    $nodeAddress,
                    $name,
                    strtolower($name),
                ]);
            } catch (PDOException $exception) {
                if (
                    strpos($exception->getMessage(), 'UNIQUE') !== false
                ) {
                    respond(
                        'Fehler: Dieser Name ist bereits vergeben.'
                    );
                }

                throw $exception;
            }

            respond('Registriert als ' . $name . '.');

        case 'deregister':
            if ($currentUser === null) {
                respond('Fehler: Du bist nicht registriert.');
            }

            $statement = $db->prepare(
                'DELETE FROM users WHERE id = ?'
            );

            $statement->execute([$currentUser['id']]);

            respond('Benutzerkonto gelöscht.');

        case 'users':
            $names = $db
                ->query(
                    'SELECT name
                     FROM users
                     ORDER BY name_key'
                )
                ->fetchAll(PDO::FETCH_COLUMN);

            if (count($names) === 0) {
                respond('Keine Benutzer registriert.');
            }

            respond('Benutzer: ' . implode(', ', $names));

        case 'send':
            if ($currentUser === null) {
                respond('Fehler: Du bist nicht registriert.');
            }

            $recipientName = parameter(2);

            /*
             * Die Nachricht besteht aus allen Parametern ab p03.
             * Dadurch bleiben auch Nachrichten mit Leerzeichen erhalten.
             */
            $messageParts = [];

            for ($index = 3; $index <= 10; $index++) {
                $part = parameter($index);

                if ($part !== '') {
                    $messageParts[] = $part;
                }
            }

            $message = trim(implode(' ', $messageParts));

            if ($recipientName === '' || $message === '') {
                respond(
                    'Fehler: send EMPFÄNGER NACHRICHT'
                );
            }

            if (textLength($message) > 160) {
                respond(
                    'Fehler: Die Nachricht darf maximal '
                    . '160 Zeichen lang sein.'
                );
            }

            $statement = $db->prepare(
                'SELECT id
                 FROM users
                 WHERE name_key = ?'
            );

            $statement->execute([
                strtolower($recipientName),
            ]);

            $recipientId = $statement->fetchColumn();

            if ($recipientId === false) {
                respond('Fehler: Empfänger nicht gefunden.');
            }

            $statement = $db->prepare(
                'INSERT INTO mails (
                    recipient_id,
                    sender_name,
                    message,
                    created_at
                ) VALUES (?, ?, ?, ?)'
            );

            $statement->execute([
                $recipientId,
                $currentUser['name'],
                $message,
                date('Y-m-d H:i:s'),
            ]);

            respond('Nachricht gespeichert.');

        case 'read':
            if ($currentUser === null) {
                respond('Fehler: Du bist nicht registriert.');
            }

            /*
             * Sperre verhindert, dass dieselbe Nachricht bei zwei
             * gleichzeitigen Aufrufen doppelt ausgegeben wird.
             */
            $db->exec('BEGIN IMMEDIATE');

            $statement = $db->prepare(
                'SELECT
                    id,
                    sender_name,
                    message,
                    created_at
                 FROM mails
                 WHERE recipient_id = ?
                 ORDER BY id
                 LIMIT 1'
            );

            $statement->execute([$currentUser['id']]);
            $mail = $statement->fetch(PDO::FETCH_ASSOC);

            if ($mail === false) {
                $db->exec('COMMIT');
                respond('Keine Nachrichten.');
            }

            $statement = $db->prepare(
                'DELETE FROM mails WHERE id = ?'
            );

            $statement->execute([$mail['id']]);
            $db->exec('COMMIT');

            respond(
                $mail['sender_name']
                . ' '
                . $mail['created_at']
                . ":\n"
                . $mail['message']
            );

        case 'status':
            if ($currentUser === null) {
                respond('Fehler: Du bist nicht registriert.');
            }

            $statement = $db->prepare(
                'SELECT COUNT(*)
                 FROM mails
                 WHERE recipient_id = ?'
            );

            $statement->execute([$currentUser['id']]);
            $mailCount = (int)$statement->fetchColumn();

            respond(
                'Benutzer: '
                . $currentUser['name']
                . "\nNachrichten: "
                . $mailCount
            );

        default:
            respond(
                "Unbekannter Befehl.\n"
                . helpText()
            );
    }
} catch (Throwable $exception) {
    if (
        isset($db)
        && $db instanceof PDO
        && $db->inTransaction()
    ) {
        $db->rollBack();
    }

    respond('Mailbot-Fehler: ' . $exception->getMessage());
}