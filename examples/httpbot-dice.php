<?php
declare(strict_types=1);

header('Content-Type: text/plain; charset=utf-8');

$name = trim((string) ($_GET['name'] ?? ''));
if ($name === '') {
    $name = trim((string) ($_GET['shortname'] ?? ''));
}
if ($name === '') {
    $name = 'Spieler';
}

$requestedCount = filter_input(INPUT_GET, 'p01', FILTER_VALIDATE_INT);
$count = $requestedCount === false || $requestedCount === null ? 1 : $requestedCount;
$count = max(1, min(20, $count));

$numbers = [];
for ($index = 0; $index < $count; $index++) {
    $numbers[] = (string) random_int(1, 6);
}

echo 'Hallo ' . $name . ', deine Zahlen sind ' . implode('  ', $numbers);
