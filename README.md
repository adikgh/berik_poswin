# Berik POS — Windows офлайн касса

pos.berik.c кассасын интернетсіз жұмыс істейтін Windows программасы етіп орайды.
Windows 7 SP1, 8, 10, 11 (64-бит).

## Қалай жұмыс істейді
- `BerikPOS.exe` (C#, .NET Framework 4.8) ішінде жасырын MariaDB (жергілікті база) мен PHP қосылады,
  касса WebView2 терезесінде ашылады. Касса коды — `berik_pos` репосының өзі (жинағанда алынады).
- Бірінші кіру: сервер адресі + телефон + PIN. Меню, қызметкерлер, клиенттер серверден тартылады.
- Заказдар, тарих, сменалар жергілікті базаға жазылады және серверге жүктеледі:
  «Серверге жүктеу» батырмасымен немесе интернет бар болса минут сайын (байланыс сапасы тексеріледі).
- Офлайнда тек Кафе, Собой, Сервис. Жеткізу мен кэшбэк өшірулі.
- Чек Windows-тағы әдепкі принтерге диалогсыз шығады (`--kiosk-printing`).
- Деректер: `C:\ProgramData\BerikPOS` (база, логтар, баптаулар). Программа: `C:\BerikPOS`.

## Серверге керек
- `berik_pos` ішінде `api/sync/` (login, pull, push, ping).
- SQL: `server/berik_offline_server.sql` (company_branch.offline, company_device, offline_uuid бағандары).
- Админде точканы офлайн деп белгілеу: `company_branch.offline = 1`.

## Жинау және шығару
- `v1.0.0` тегін push етсе, GitHub Actions `BerikPOS-Setup-1.0.0.exe` жасап, Release-ке шығарады.
  Орнатылған программалар жаңа нұсқаны өздері тауып, жаңартады.
- Сервер адресін алдын ала қою: репо Settings → Variables → `BERIK_SERVER` (мысалы `https://pos.example.kz`).

## Құрылым
- `src/BerikPOS` — Windows программасы
- `www/local` — кассадағы синхрон агенті (PHP)
- `build/prepare.ps1` — PHP 8.2, MariaDB 10.4, касса сайтын жинау
- `installer/BerikPOS.iss` — Inno Setup орнатқышы
