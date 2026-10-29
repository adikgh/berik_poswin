<?php
	// Жергілікті синхрон: ?a=status | setup | menu | push | auto
	//  setup — бірінші кіру (сервер, компанияның 4 санды коды, точка) + бүкіл деректі тарту
	//  menu  — менюді (анықтамалықтарды) серверден қайта тарту
	//  push  — офлайн жазбаларды серверге жүктеу (батырма)
	//  auto  — байланыс сапасын тексеріп, жақсы болса push (программа өзі шақырады)
	require __DIR__.'/lib.php';

	$a = @$_GET['a'] ?: 'status';
	$in = json_decode(file_get_contents('php://input'), true) ?: $_POST;

	if ($a == 'status') {
		$s = settings();
		$pending = 0;
		try {
			ensure_local_cols();
			$pending = (int)@row("SELECT count(*) c FROM retail_orders WHERE paid = 1 AND (synced_at IS NULL OR sync_hash IS NULL)")['c'];
		} catch (Throwable $e) {}
		out(['ok' => true, 'ver' => APP_VER, 'linked' => !empty($s['token']), 'server' => @$s['server'], 'company_id' => @$s['company_id'], 'branch_id' => @$s['branch_id'],
			'last_push' => @$s['last_push'], 'last_pull' => @$s['last_pull'], 'pending' => $pending]);
	}

	if ($a == 'setup') {
		$server = trim((string)@$in['server']);
		if (!preg_match('#^https?://[a-z0-9.\-]+(:\d+)?(/.*)?$#i', $server)) fail('bad_server');
		$s = settings(); $s['server'] = rtrim($server, '/'); unset($s['token']); save_settings($s);
		$r = api('login.php', ['company_code' => @$in['company_code'], 'branch_id' => (int)@$in['branch_id'], 'device_name' => gethostname()]);
		if (!$r['json']) fail('no_connection', 502, ['detail' => $r['err']]);
		if (!@$r['json']['ok']) out($r['json'], 400);
		$s['token'] = $r['json']['token']; $s['device_id'] = $r['json']['device_id'];
		$s['company_id'] = $r['json']['company_id']; $s['branch_id'] = $r['json']['branch_id'];
		save_settings($s);
		$lock = lock();
		$p = pull();
		$p['assets'] = fetch_assets();
		out($p);
	}

	if ($a == 'menu') { $lock = lock(); $p = push(); $r = ['push' => $p] + pull(); if (!is_file(FA_DIR.'/css/all.min.css')) $r['assets'] = fetch_assets(); out($r); }
	if ($a == 'push') { $lock = lock(); out(push()); }
	if ($a == 'auto') {
		$s = settings();
		if (empty($s['token'])) out(['ok' => false, 'error' => 'not_linked']);
		$n = net_quality();
		if (!$n['good']) out(['ok' => false, 'error' => 'weak_net', 'net' => $n]);
		$lock = lock();
		out(['net' => $n] + push());
	}
	fail('bad_action');


	// ---------- Иконкалар (Font Awesome) ----------
	// Репода жоқ (лицензия), сондықтан бірінші кіргенде серверден бір рет жүктеледі
	function fetch_assets() {
		$s = settings(); $base = rtrim($s['server'], '/').'/assets/pl/fontawesome/';
		$get = function ($u) {
			$ch = curl_init($u);
			curl_setopt_array($ch, [CURLOPT_RETURNTRANSFER => true, CURLOPT_CONNECTTIMEOUT => 8, CURLOPT_TIMEOUT => 120, CURLOPT_FOLLOWLOCATION => true]);
			if (ini_get('curl.cainfo')) curl_setopt($ch, CURLOPT_CAINFO, ini_get('curl.cainfo'));
			$b = curl_exec($ch); $c = curl_getinfo($ch, CURLINFO_HTTP_CODE); curl_close($ch);
			return $c == 200 ? $b : null;
		};
		$css = $get($base.'css/all.min.css');
		if (!$css) return ['ok' => false];
		@mkdir(FA_DIR.'/css', 0777, true); @mkdir(FA_DIR.'/webfonts', 0777, true);
		$n = 0;
		preg_match_all('#url\(["\']?\.\./webfonts/([a-z0-9._-]+)#i', $css, $m);
		foreach (array_unique($m[1]) as $f) { $b = $get($base.'webfonts/'.$f); if ($b) { file_put_contents(FA_DIR.'/webfonts/'.$f, $b); $n++; } }
		file_put_contents(FA_DIR.'/css/all.min.css', $css);
		return ['ok' => true, 'fonts' => $n];
	}


	// ---------- Серверден тарту ----------
	function pull() {
		$r = api('pull.php', null, 300);
		if (!$r['json']) fail('no_connection', 502, ['detail' => $r['err']]);
		if (!@$r['json']['ok']) out($r['json'], 400);
		$j = $r['json'];
		$have = tables();
		q("SET FOREIGN_KEY_CHECKS = 0");

		// 1) Құрылым: жоқ кестелерді жасау, бар кестелерге жетіспейтін бағандарды қосу
		foreach ($j['schema'] as $t => $create) {
			if (!preg_match('/^[a-z0-9_]+$/i', $t)) continue;
			$create = preg_replace('/ AUTO_INCREMENT=\d+/', '', $create);
			// Жергілікті MariaDB ескірек болуы мүмкін: жаңа collation-дарды ортақ түріне ауыстырамыз
			$create = preg_replace('/utf8mb4_(0900|uca1400)_[a-z_]+/', 'utf8mb4_unicode_ci', $create);
			$create = preg_replace('/utf8mb3_uca1400_[a-z_]+/', 'utf8mb3_unicode_ci', $create);
			if (!in_array($t, $have)) { q($create); continue; }
			$lc = cols($t);
			foreach (explode("\n", $create) as $line) {
				if (preg_match('/^\s*`([^`]+)`\s+(.+?),?$/', $line, $m) && !isset($lc[$m[1]])) {
					try { q("ALTER TABLE `$t` ADD `{$m[1]}` ".rtrim($m[2], ',')); } catch (Throwable $e) {}
				}
			}
		}

		// 2) Анықтамалық деректер: толық ауыстыру (заказ/смена кестелеріне тимейді)
		q("START TRANSACTION");
		foreach ($j['data'] as $t => $list) {
			if (in_array($t, OWN_TABLES) || !preg_match('/^[a-z0-9_]+$/i', $t)) continue;
			$keep = [];
			// кассада жасалған, әлі серверге жетпеген клиенттерді сақтаймыз
			if ($t == 'company_client' && in_array($t, $have)) {
				// серверге жеткен клиенттер (телефоны сәйкес) серверлік id-ге ауысады
				$srv = []; foreach ($list as $c) if (!empty($c['phone'])) $srv[$c['phone']] = (int)$c['id'];
				foreach (rows("SELECT * FROM company_client WHERE id >= 900000000") as $c) {
					if (isset($srv[$c['phone']])) q("UPDATE retail_orders SET client_id = '".$srv[$c['phone']]."' WHERE client_id = '".(int)$c['id']."'");
					else $keep[] = $c;
				}
			}
			q("DELETE FROM `$t`");
			$lc = cols($t);
			foreach (array_merge($list, $keep) as $row) {
				$f = []; $v = [];
				foreach ($row as $k => $val) if (isset($lc[$k])) { $f[] = "`$k`"; $v[] = sqlv($val); }
				if ($f) q("INSERT INTO `$t` (".implode(',', $f).") VALUES (".implode(',', $v).")");
			}
		}
		// Офлайнда жеткізу жоқ: тек Кафе, Собой, Сервис
		if (isset(cols('company')['delivery'])) q("UPDATE company SET delivery = 0");
		q("COMMIT");
		// Кассада қосылған клиенттер серверден келгендермен қақтығыспасын
		if (in_array('company_client', tables())) q("ALTER TABLE company_client AUTO_INCREMENT = 900000000");
		q("SET FOREIGN_KEY_CHECKS = 1");
		ensure_local_cols();

		$s = settings(); $s['last_pull'] = date('Y-m-d H:i:s'); save_settings($s);
		return ['ok' => true, 'pulled' => array_map('count', $j['data']), 'time' => $s['last_pull']];
	}


	// ---------- Серверге жүктеу ----------
	function push() {
		ensure_local_cols();
		// uuid жоқ жазбаларға uuid беру
		foreach (['retail_orders', 'retail_shifts', 'retail_orders_log'] as $t)
			if (in_array($t, tables())) foreach (rows("SELECT id FROM `$t` WHERE offline_uuid IS NULL") as $r) q("UPDATE `$t` SET offline_uuid = '".uuid()."' WHERE id = '".(int)$r['id']."'");

		// Төленген заказдар: жүктелмегендер + соңғы 3 күнде өзгергендер (хэш бойынша)
		$since = date('Y-m-d H:i:s', strtotime('-3 day'));
		$orders = rows("SELECT * FROM retail_orders WHERE paid = 1 AND (synced_at IS NULL OR ins_dt >= '$since' OR upd_dt >= '$since') ORDER BY id");
		$send = []; $hash = [];
		$has_log = in_array('retail_orders_log', tables());
		foreach ($orders as $o) {
			$oid = (int)$o['id'];
			$prod = rows("SELECT * FROM retail_orders_products WHERE order_id = '$oid' ORDER BY id");
			foreach ($prod as &$p) unset($p['id'], $p['order_id']); unset($p);
			$log = [];
			if ($has_log) foreach (rows("SELECT * FROM retail_orders_log WHERE order_id = '$oid' ORDER BY id") as $l) {
				$l['uuid'] = $l['offline_uuid']; unset($l['id'], $l['order_id'], $l['offline_uuid']); $log[] = $l;
			}
			$client = null;
			if ((int)@$o['client_id']) {
				$client = row("SELECT * FROM company_client WHERE id = '".(int)$o['client_id']."'");
				if ($client) unset($client['id'], $client['company_id'], $client['user_id']);
			}
			$ord = $o; $u = $o['offline_uuid'];
			unset($ord['id'], $ord['offline_uuid'], $ord['server_id'], $ord['synced_at'], $ord['sync_hash'], $ord['client_id']);
			$item = ['uuid' => $u, 'order' => $ord, 'products' => $prod, 'log' => $log, 'client' => $client];
			$h = md5(json_encode($item));
			if ($h === $o['sync_hash'] && $o['synced_at']) continue;
			$send[] = $item; $hash[$u] = [$oid, $h];
		}

		$shifts = []; $shash = [];
		if (in_array('retail_shifts', tables())) foreach (rows("SELECT * FROM retail_shifts WHERE synced_at IS NULL OR sync_hash IS NULL OR upd_dt >= '$since'") as $sh) {
			$u = $sh['offline_uuid']; $sid = (int)$sh['id'];
			unset($sh['id'], $sh['offline_uuid'], $sh['server_id'], $sh['synced_at'], $sh['sync_hash']);
			$item = ['uuid' => $u, 'shift' => $sh]; $h = md5(json_encode($item));
			$old = row("SELECT sync_hash, synced_at FROM retail_shifts WHERE id = '$sid'");
			if ($old['synced_at'] && $old['sync_hash'] === $h) continue;
			$shifts[] = $item; $shash[$u] = [$sid, $h];
		}

		if (!$send && !$shifts) return ['ok' => true, 'sent' => 0, 'shifts' => 0];

		$done = 0; $sdone = 0; $errors = [];
		// 50-50 бөліп жібереміз, интернет баяу болса да бөлігі жетеді
		foreach (array_chunk($send ?: [null], 50) as $i => $chunk) {
			$payload = ['orders' => array_values(array_filter($chunk)), 'shifts' => $i == 0 ? $shifts : []];
			$r = api('push.php', $payload, 120);
			if (!$r['json'] || !@$r['json']['ok']) { $errors[] = $r['json']['error'] ?? ($r['err'] ?: 'http_'.$r['code']); break; }
			$now = date('Y-m-d H:i:s');
			foreach ($r['json']['orders'] as $u => $sid) if (isset($hash[$u])) {
				q("UPDATE retail_orders SET server_id = '".(int)$sid."', synced_at = '$now', sync_hash = '".$hash[$u][1]."' WHERE id = '".$hash[$u][0]."'"); $done++;
			}
			foreach ($r['json']['shifts'] as $u => $sid) if (isset($shash[$u])) {
				q("UPDATE retail_shifts SET server_id = '".(int)$sid."', synced_at = '$now', sync_hash = '".$shash[$u][1]."' WHERE id = '".$shash[$u][0]."'"); $sdone++;
			}
			foreach ((array)@$r['json']['errors'] as $e) $errors[] = $e;
		}
		$s = settings(); if (!$errors) $s['last_push'] = date('Y-m-d H:i:s'); save_settings($s);
		return ['ok' => !$errors, 'sent' => $done, 'shifts' => $sdone, 'left' => count($send) - $done, 'errors' => $errors];
	}
