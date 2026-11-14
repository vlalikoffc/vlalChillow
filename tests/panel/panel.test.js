/**
 * Проверка живой страницы панели (server/Web/wwwroot/index.html) в jsdom.
 *
 * Сеть не нужна: fetch замокан ответами, которые отдаёт WebPanelRouter, поэтому тест
 * проверяет ровно то, что сломается в браузере, — до того, как это увидит игрок:
 *   • каталог режимов берётся с сервера, а не из копии в JS;
 *   • фильтры режима и карты работают, «Все режимы» выбран явно;
 *   • создание комнаты уходит одним POST, номер выдаёт сервер;
 *   • ошибки API показываются понятным текстом, а не кодом;
 *   • имя комнаты экранируется (XSS из чужого ввода);
 *   • закрытая комната не приглашает войти;
 *   • баннер появляется, когда игровой порт не слушается.
 */
const fs = require('fs');
const path = require('path');
/* jsdom ставится отдельно (страница — единственный JS в проекте):
     npm install --prefix /tmp/paneltest jsdom
     NODE_PATH=/tmp/paneltest/node_modules node tests/panel/panel.test.js
   Путь к jsdom не зашит: бери тот, что видит Node. */
const { JSDOM } = require('jsdom');

const html = fs.readFileSync(
  path.join(__dirname, '..', '..', 'server', 'Web', 'wwwroot', 'index.html'), 'utf8');

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, extra) {
  if (cond) { pass++; }
  else { fail++; failures.push(name + (extra ? ' — ' + extra : '')); }
}

/* ── данные, как их отдаёт сервер ─────────────────────────────────────── */
const MODES = {
  modes: [
    { id: 'Ranked2v2', name: 'Союзники', alias: '2v2', defaultLevel: 'Sandstone 2x2',
      levels: ['Prison 2x2', 'Sandstone 2x2'], aliases: ['2v2', 'союзники'],
      teamSizes: [1, 2] },
    { id: 'Duel', name: 'Дуэль', alias: 'duel', defaultLevel: 'Yard',
      levels: ['Block', 'Yard'], aliases: ['duel'], teamSizes: [1, 2] },
    { id: 'DeathMatch', name: 'Командный бой', alias: 'tdm', defaultLevel: 'Arena',
      levels: ['Arena'], aliases: ['tdm'], teamSizes: [] },
  ],
};

let roomsState = [
  { number: 1, mode: 'Ranked2v2', level: 'Sandstone 2x2', name: 'рялна',
    owner: 'vlal', state: 'live', players: 4, joinCommand: '/join 2v2 #1',
    perTeam: 2, maxPlayers: 4, teamLabel: '2 на 2' },
  { number: 2, mode: 'Duel', level: 'Yard', name: '', owner: 'botx',
    state: 'waiting', players: 1, joinCommand: '/join duel #2' },
];

let statusState = {
  onlineLobby: 2, onlineMatch: 5, onlineTotal: 7,
  roomsCount: 2, roomsMax: 64, nextNumber: 3, gameListening: true,
};

let posted = null;
let postResponse = null;

function fakeFetch(url, opts) {
  const method = (opts && opts.method) || 'GET';
  const u = String(url);
  let body = null, status = 200;

  if (u.indexOf('api/modes') >= 0) body = MODES;
  else if (u.indexOf('api/rooms') >= 0 && method === 'GET') {
    body = { nextNumber: 3, count: roomsState.length, maxRooms: 64, rooms: roomsState };
  } else if (u.indexOf('api/rooms') >= 0 && method === 'POST') {
    posted = JSON.parse(opts.body);
    if (postResponse) { body = postResponse.body; status = postResponse.status; }
    else {
      body = { number: 3, mode: 'Ranked2v2', modeName: 'Союзники', level: 'Sandstone 2x2',
               name: 'новая', owner: 'новичок', joinCommand: '/join 2v2 #3', state: 'waiting' };
      status = 201;
    }
  } else if (u.indexOf('api/status') >= 0) body = statusState;
  else { body = null; status = 404; }

  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => null },
    text: () => Promise.resolve(body === null ? '' : JSON.stringify(body)),
  });
}

(async () => {
  const dom = new JSDOM(html, {
    runScripts: 'dangerously',
    pretendToBeVisual: true,
    url: 'https://example.com/vlalchillow/',
    beforeParse(window) {
      window.fetch = fakeFetch;
      window.matchMedia = () => ({ matches: false, addListener() {}, removeListener() {} });
      window.scrollTo = () => {};
    },
  });

  const { window } = dom;
  await new Promise(r => setTimeout(r, 120));

  const P = window.__panel;
  const $ = id => window.document.getElementById(id);

  check('window.__panel есть', !!P);
  if (!P) { report(); return; }

  /* ── каталог пришёл с сервера ── */
  check('каталог загружен с /api/modes', P.MODES.length === 3, 'MODES=' + P.MODES.length);
  check('селект режимов заполнен', $('mode').options.length === 3, 'options=' + $('mode').options.length);
  check('фильтр режимов начинается с «Все режимы»',
    $('f-mode').options[0].value === '' && $('f-mode').selectedIndex === 0);

  /* ── комнаты с сервера ── */
  check('комнаты загружены', P.rooms.length === 2, 'rooms=' + P.rooms.length);
  P.switchTab('list');
  check('строк в списке столько же, сколько комнат',
    $('rooms').children.length === 2, 'rows=' + $('rooms').children.length);
  check('номер комнаты на месте', $('rooms').textContent.indexOf('#1') >= 0);
  check('состояние live отрисовано', $('rooms').textContent.indexOf('идёт') >= 0);
  check('состояние waiting отрисовано', $('rooms').textContent.indexOf('ожидание') >= 0);

  /* ── фильтры ── */
  $('f-mode').value = 'Duel';
  $('f-mode').dispatchEvent(new window.Event('change'));
  check('фильтр по режиму сужает список', $('rooms').children.length === 1,
    'rows=' + $('rooms').children.length);

  $('f-mode').value = '';
  $('f-mode').dispatchEvent(new window.Event('change'));
  check('сброс фильтра возвращает всё', $('rooms').children.length === 2);

  $('f-map').value = 'Yard';
  $('f-map').dispatchEvent(new window.Event('change'));
  check('фильтр по карте работает', $('rooms').children.length === 1);
  $('f-map').value = '';
  $('f-map').dispatchEvent(new window.Event('change'));

  /* ── статистика ── */
  check('онлайн всего с сервера', $('s-total').textContent === '7', $('s-total').textContent);
  check('в лобби с сервера', $('s-lobby').textContent === '2', $('s-lobby').textContent);
  check('в матчах с сервера', $('s-match').textContent === '5', $('s-match').textContent);
  check('комнат из сервера', $('s-rooms').textContent.indexOf('/ 64') >= 0, $('s-rooms').textContent);

  /* ── предупреждение о порте ── */
  check('порт слушается — баннера нет', $('s-warn').hidden === true);
  statusState = Object.assign({}, statusState, { gameListening: false });
  await P && window.eval('');
  await new Promise(r => setTimeout(r, 2200));  // тик живого обновления
  check('порт не слушается — баннер показан', $('s-warn').hidden === false,
    'hidden=' + $('s-warn').hidden);
  statusState = Object.assign({}, statusState, { gameListening: true });

  /* ── создание комнаты ── */
  P.switchTab('create');
  $('nick').value = 'новичок';
  $('mode').selectedIndex = 0;
  $('mode').dispatchEvent(new window.Event('change'));
  $('room-name').value = 'новая';
  P.createRoom();
  await new Promise(r => setTimeout(r, 120));

  check('POST ушёл с режимом/картой/ником',
    posted && posted.mode === 'Ranked2v2' && posted.owner === 'новичок' && posted.level === 'Sandstone 2x2',
    JSON.stringify(posted));
  check('команда /join показана', $('result-cmd').textContent === '/join 2v2 #3',
    $('result-cmd').textContent);
  check('результат виден', $('result').hidden === false);
  check('комната добавлена в список', P.rooms.length === 3, 'rooms=' + P.rooms.length);
  check('поле имени очищено', $('room-name').value === '');

  /* ── ошибки сервера ── */
  postResponse = { status: 409, body: { code: 'one_room_per_player', message: 'x' } };
  $('nick').value = 'другой';
  P.createRoom();
  await new Promise(r => setTimeout(r, 120));
  check('409 показан понятным текстом',
    $('create-err').textContent.indexOf('уже есть комната') >= 0, $('create-err').textContent);
  check('после ошибки новых комнат не появилось', P.rooms.length === 3);

  postResponse = { status: 429, body: { code: 'rate' } };
  P.createRoom();
  await new Promise(r => setTimeout(r, 120));
  check('429 объяснён', $('create-err').textContent.indexOf('слишком часто') >= 0,
    $('create-err').textContent);

  postResponse = null;

  /* ── пустой ник не уходит на сервер ── */
  posted = null;
  $('nick').value = '   ';
  P.createRoom();
  await new Promise(r => setTimeout(r, 60));
  check('пустой ник не создаёт запрос', posted === null);
  check('пустой ник объяснён', $('create-err').textContent.indexOf('укажи ник') >= 0);

  /* ── XSS: имя комнаты экранируется ── */
  P.setRooms([{ n: 9, name: '<img src=x onerror=alert(1)>', mode: 'Duel',
                map: 'Yard', players: 0, state: 'waiting', owner: 'x' }]);
  P.switchTab('list');
  check('имя комнаты экранировано',
    $('rooms').querySelector('img') === null && $('rooms').textContent.indexOf('<img') >= 0);

  /* ── мёртвая комната не приглашает ── */
  P.setRooms([{ n: 5, name: '', mode: 'Duel', map: 'Yard', players: 0,
                state: 'finished', owner: 'x' }]);
  P.switchTab('list');
  const deadBtn = $('rooms').querySelector('button');
  check('закрытая комната: кнопка неактивна', deadBtn && deadBtn.disabled === true);
  check('закрытая комната подписана', $('rooms').textContent.indexOf('закрыта') >= 0);

  /* ── выбор размера команды ── */
  // Союзники: размер выбирается, по умолчанию — наибольший (2 на 2).
  P.setMode(MODES.modes);
  $('mode').value = 'Ranked2v2';
  $('mode').dispatchEvent(new window.Event('change'));

  check('союзники: выбор размера виден', $('team-section').hidden === false);
  check('союзники: два варианта (1 и 2)',
    $('teams').children.length === 2);
  check('союзники: подписи «1 на 1» и «2 на 2»',
    $('teams').textContent.indexOf('1 на 1') >= 0 &&
    $('teams').textContent.indexOf('2 на 2') >= 0);

  const chosen = Array.from($('teams').children)
    .filter(b => b.getAttribute('aria-checked') === 'true');
  check('союзники: выбран ровно один вариант', chosen.length === 1);
  check('союзники: по умолчанию 2 на 2',
    chosen[0] && chosen[0].textContent === '2 на 2');
  check('союзники: подпись про 4 человека',
    $('team-note').textContent.indexOf('4') >= 0);

  // Переключение на 1 на 1 — выбор запоминается и уходит на сервер.
  const oneOnOne = Array.from($('teams').children)
    .find(b => b.textContent === '1 на 1');
  oneOnOne.dispatchEvent(new window.Event('click'));
  check('1 на 1: стало выбрано',
    oneOnOne.getAttribute('aria-checked') === 'true' &&
    $('teams').querySelectorAll('[aria-checked="true"]').length === 1);
  check('1 на 1: подпись про 2 человека',
    $('team-note').textContent.indexOf('2') >= 0);

  // Режим, где размер не выбирается: секция обязана исчезнуть, а не показать пустоту.
  $('mode').value = 'DeathMatch';
  $('mode').dispatchEvent(new window.Event('change'));
  check('tdm: выбор размера скрыт', $('team-section').hidden === true);

  // Возврат на союзников — прежний выбор на месте, а не сброшен на дефолт.
  $('mode').value = 'Ranked2v2';
  $('mode').dispatchEvent(new window.Event('change'));
  const again = Array.from($('teams').children)
    .find(b => b.getAttribute('aria-checked') === 'true');
  check('возврат на союзников: выбор помнится',
    again && again.textContent === '1 на 1');

  // Создание комнаты отправляет perTeam.
  posted = null;
  $('nick').value = 'влал999';
  $('create-btn').dispatchEvent(new window.Event('click'));
  return new Promise(resolve => setTimeout(resolve, 30)).then(() => {
    check('perTeam уходит на сервер', posted && posted.perTeam === 1);

    /* ── потолок комнаты берётся из самой комнаты ── */
    P.setRooms([{ n: 7, name: 'малая', mode: 'Ranked2v2', map: 'Sandstone 2x2',
                  players: 2, state: 'waiting', owner: 'x',
                  perTeam: 1, maxPlayers: 2, teamLabel: '1 на 1' }]);
    P.switchTab('list');
    const row = $('rooms').textContent;
    check('счётчик по потолку комнаты: 2/2', row.indexOf('2/2') >= 0);
    check('подпись размера в списке', row.indexOf('1 на 1') >= 0);

    report();
  });

  function report() {
    console.log('\n──────────────────────────────────────────');
    if (fail === 0) console.log('ВСЁ ЗЕЛЁНО: ' + pass + ' проверок');
    else {
      console.log('ПРОВАЛОВ: ' + fail + ' из ' + (pass + fail));
      failures.forEach(f => console.log('  ✗ ' + f));
    }
    console.log('──────────────────────────────────────────');
    window.close();
    process.exit(fail === 0 ? 0 : 1);
  }
})();
