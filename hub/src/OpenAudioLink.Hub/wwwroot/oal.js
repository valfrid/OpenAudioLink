/*
 * What every source app needs, in one place.
 *
 * The apps differ only in what they let you pick — a station, a turntable,
 * nothing at all in Spotify's case. Everything after that is identical:
 * choose a cast point, press play, set the volume, stop. So that half
 * lives here and each app supplies the other half.
 *
 * No framework and no build step, deliberately. The control role is meant
 * to be hostable on a node one day, and a node can serve a file it does
 * not have to compile.
 */
const OAL = (() => {
  /** HTML-escapes anything a person typed — station names, room names. */
  const esc = value => String(value ?? '').replace(/[&<>"']/g, c => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));

  /**
   * Every call goes through here so a failure reaches a person as the
   * sentence the Hub wrote, rather than as nothing happening. Silent
   * guards have produced three "nothing happens" bugs in this project.
   */
  async function api(method, path, body) {
    const response = await fetch(path, {
      method,
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    });
    const text = await response.text();
    const payload = text ? JSON.parse(text) : null;
    if (!response.ok) {
      throw new Error(payload?.error ?? `${method} ${path} failed (${response.status})`);
    }
    return payload;
  }

  /**
   * Everything an app view needs, in one round trip's worth of requests.
   *
   * Stations are fetched even by apps that do not show them, because the
   * portal has to say whether Radio has anything in it — an app tile that
   * opens onto an empty list is worse than one that says it is empty.
   */
  async function load() {
    const [castPoints, devices, stations, stream] = await Promise.all([
      api('GET', '/api/castpoints'),
      api('GET', '/api/devices'),
      api('GET', '/api/stations').catch(() => []),
      api('GET', '/api/stream').catch(() => null),
    ]);
    return {
      castPoints: castPoints ?? [],
      devices: devices ?? [],
      stations: stations ?? [],
      stream,
      hub: (devices ?? []).find(d => d.hardwareProfile === 'windows-hub') ?? null,
      // Nodes with an ADC, which is what a vinyl app has to choose between.
      lineIn: (devices ?? []).filter(d =>
        d.online && (d.roles ?? []).includes('producer') && d.status?.input),
      playing: (castPoints ?? []).find(p => p.playing) ?? null,
    };
  }

  /**
   * The cast point a page should act on.
   *
   * Reads #room= first, because that is what an NFC sticker on a wall
   * carries and somebody who just waved a phone at one has already said
   * where they want the sound. Matches on id, then on a slug of the name,
   * so a sticker written by hand as "#room=matsal" finds "Matsal".
   */
  function roomFromUrl(castPoints) {
    const wanted = decodeURIComponent((location.hash.match(/room=([^&]+)/) ?? [])[1] ?? '');
    if (!wanted) return null;
    const slug = value => String(value).toLowerCase().replace(/[^a-z0-9]+/g, '');
    return castPoints.find(p => p.id === wanted)
      ?? castPoints.find(p => slug(p.name) === slug(wanted))
      ?? null;
  }

  /**
   * Fills a <select> with the cast points, keeping the current choice if it
   * still exists. A room with no speakers in it is offered but marked,
   * rather than hidden: an empty room is a setup mistake somebody can fix,
   * and a room that silently vanished is a mystery.
   */
  function fillRooms(select, castPoints, preferred) {
    const previous = select.value;
    select.innerHTML = castPoints.map(point => {
      const empty = (point.members ?? []).length === 0;
      const offline = !empty && (point.members ?? []).every(m => !m.online);
      const note = empty ? ' — no speakers yet' : offline ? ' — speakers offline' : '';
      return `<option value="${esc(point.id)}">${esc(point.name)}${note}</option>`;
    }).join('');

    const wanted = [preferred, previous].find(id =>
      id && castPoints.some(p => p.id === id));
    if (wanted) select.value = wanted;
  }

  /**
   * Volume is sent while the slider is still moving, but not on every
   * pixel: a drag across the bar is a hundred events, and each one is an
   * HTTP call that fans out to every speaker in the room.
   */
  function volumeSender(getRoom, onError) {
    const INTERVAL_MS = 250;
    let pending = null;
    let timer = null;

    async function send() {
      timer = null;
      const percent = pending;
      const room = getRoom();
      pending = null;
      if (percent === null || !room) return;
      try {
        await api('POST', `/api/castpoints/${encodeURIComponent(room)}/volume`,
          { percent });
      } catch (error) {
        onError?.(error.message);
      }
    }

    return percent => {
      pending = percent;
      if (timer === null) timer = setTimeout(send, INTERVAL_MS);
    };
  }

  /** Whether this cast point is the one currently making sound. */
  const isPlaying = (state, roomId) => state.playing?.id === roomId;

  return { esc, api, load, roomFromUrl, fillRooms, volumeSender, isPlaying };
})();
