<?php
	// Офлайн касса: жергілікті синхрон агенті (кассаның өз PHP-сінде, 127.0.0.1-де жұмыс істейді).
	// Баптаулар: %ProgramData%\BerikPOS\settings.json (server, token, device_id, company_id, branch_id).

	if (!in_array(@$_SERVER['REMOTE_ADDR'], ['127.0.0.1', '::1'])) { http_response_code(403); exit('local only'); }

	require dirname(__DIR__).'/site/config/db.php';
	new db;
	mysqli_set_charset(db::$connect, 'utf8mb4');
	mysqli_report(MYSQLI_REPORT_ERROR | MYSQLI_REPORT_STRICT);
	date_default_timezone_set('Asia/Aqtau');
	header('Content-Type: application/json; charset=utf-8');
	header('Cache-Control: no-store');
	@set_time_limit(300);

	const APP_VER = '1.0.0';
	define('DATA_DIR', getenv('BERIK_DATA') ?: (getenv('ProgramData') ?: 'C:/ProgramData').'/BerikPOS');
	define('SET_FILE', DATA_DIR.'/settings.json');
	define('LOCK_FILE', DATA_DIR.'/sync.lock');
	define('FA_DIR', dirname(__DIR__).'/site/assets/pl/fontawesome');

	// Офлайнда сақталатын кестелер (серверге жүктеледі, pull кезінде өшірілмейді)
	const OWN_TABLES = ['retail_orders', 'retail_orders_products', 'retail_orders_log', 'retail_shifts'];

	function out($d, $code = 200) { http_response_code($code); echo json_encode($d, JSON_UNESCAPED_UNICODE); exit(); }
	function fail($m, $code = 400, $extra = []) { out(['ok' => false, 'error' => $m] + $extra, $code); }
	function esc($v) { return mysqli_real_escape_string(db::$connect, (string)$v); }
	function sqlv($v) { return $v === null ? 'NULL' : "'".esc($v)."'"; }
	function q($sql) { return db::query($sql); }
	function rows($sql) { $r = []; $x = q($sql); if ($x instanceof mysqli_result) while ($a = mysqli_fetch_assoc($x)) $r[] = $a; return $r; }
	function row($sql) { $x = q($sql); return $x instanceof mysqli_result ? mysqli_fetch_assoc($x) : null; }
	function tables() { $t = []; foreach (rows("SHOW TABLES") as $r) $t[] = array_values($r)[0]; return $t; }
	function cols($t) { $c = []; foreach (rows("SHOW COLUMNS FROM `".esc($t)."`") as $r) $c[$r['Field']] = $r; return $c; }
	function uuid() { $b = random_bytes(16); $b[6] = chr(ord($b[6]) & 0x0f | 0x40); $b[8] = chr(ord($b[8]) & 0x3f | 0x80); return vsprintf('%s%s-%s-%s-%s-%s%s%s', str_split(bin2hex($b), 4)); }

	function settings() { return is_file(SET_FILE) ? (json_decode(file_get_contents(SET_FILE), true) ?: []) : []; }
	function save_settings($s) {
		if (!is_dir(DATA_DIR)) @mkdir(DATA_DIR, 0777, true);
		file_put_contents(SET_FILE.'.tmp', json_encode($s, JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT));
		rename(SET_FILE.'.tmp', SET_FILE);
	}

	// Серверге сұраныс
	function api($path, $data = null, $timeout = 60) {
		$s = settings();
		if (empty($s['server'])) fail('no_server');
		$ch = curl_init(rtrim($s['server'], '/').'/api/sync/'.$path);
		$h = ['Content-Type: application/json', 'X-App-Ver: '.APP_VER];
		if (!empty($s['token'])) $h[] = 'X-Device-Token: '.$s['token'];
		curl_setopt_array($ch, [CURLOPT_RETURNTRANSFER => true, CURLOPT_HTTPHEADER => $h, CURLOPT_CONNECTTIMEOUT => 8,
			CURLOPT_TIMEOUT => $timeout, CURLOPT_ENCODING => '']);
		if (ini_get('curl.cainfo')) curl_setopt($ch, CURLOPT_CAINFO, ini_get('curl.cainfo'));
		if ($data !== null) { curl_setopt($ch, CURLOPT_POST, true); curl_setopt($ch, CURLOPT_POSTFIELDS, json_encode($data, JSON_UNESCAPED_UNICODE)); }
		$t0 = microtime(true);
		$body = curl_exec($ch);
		$code = (int)curl_getinfo($ch, CURLINFO_HTTP_CODE);
		$err = curl_error($ch);
		curl_close($ch);
		$j = json_decode((string)$body, true);
		return ['code' => $code, 'ms' => (int)((microtime(true) - $t0) * 1000), 'err' => $err, 'json' => is_array($j) ? $j : null];
	}

	// Байланыс сапасы: 3 рет ping, бәрі сәтті және орташа уақыт 3 секундтан аз болса — жақсы
	function net_quality() {
		$ok = 0; $sum = 0;
		for ($i = 0; $i < 3; $i++) {
			$r = api('ping.php', null, 6);
			if ($r['code'] == 200 && @$r['json']['ok']) { $ok++; $sum += $r['ms']; }
		}
		return ['good' => $ok == 3 && $sum / 3 < 3000, 'ok' => $ok, 'avg_ms' => $ok ? (int)($sum / $ok) : null];
	}

	// Бір мезетте тек бір синхрон
	function lock() {
		if (!is_dir(DATA_DIR)) @mkdir(DATA_DIR, 0777, true);
		$f = fopen(LOCK_FILE, 'c');
		if (!$f || !flock($f, LOCK_EX | LOCK_NB)) fail('busy', 409);
		return $f;
	}

	// Жергілікті кестелерге синхрон бағандарын қосу
	function ensure_local_cols() {
		$all = tables();
		foreach (OWN_TABLES as $t) {
			if (!in_array($t, $all)) continue;
			$c = cols($t);
			if ($t != 'retail_orders_products' && !isset($c['offline_uuid'])) q("ALTER TABLE `$t` ADD `offline_uuid` CHAR(36) NULL, ADD UNIQUE KEY `offline_uuid` (`offline_uuid`)");
			if (in_array($t, ['retail_orders', 'retail_shifts'])) {
				if (!isset($c['server_id'])) q("ALTER TABLE `$t` ADD `server_id` INT NULL");
				if (!isset($c['synced_at'])) q("ALTER TABLE `$t` ADD `synced_at` DATETIME NULL");
				if (!isset($c['sync_hash'])) q("ALTER TABLE `$t` ADD `sync_hash` CHAR(32) NULL");
			}
		}
	}
