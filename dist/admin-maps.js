(() => {

  const form = document.getElementById('mapUpload'), status = document.getElementById('mapStatus'), progress = document.getElementById('mapProgress');

  async function missingMaps() {

    const message = document.getElementById('missingMapStatus'), list = document.getElementById('missingMapList');

    list.replaceChildren(); message.textContent = 'Checking recorded worlds…';

    try {

      const response = await fetch('/api/admin/maps/missing');

      if (!response.ok) throw new Error('Could not check missing maps. Refresh to retry.');

      const missing = await response.json();

      message.textContent = missing.length ? `${missing.length} recorded world${missing.length === 1 ? " needs" : "s need"} attention.` : 'All recorded worlds have detailed maps.';

      const requestedMap = document.getElementById('requestedMap');
      if (requestedMap.dataset.world && !missing.some(item => item.world === requestedMap.dataset.world)) requestedMap.hidden = true;
      for (const item of missing) {

        const row = document.createElement('li'), name = document.createElement('strong'), detail = document.createElement('p');

        name.textContent = item.world || 'World not recorded';

        detail.textContent = `${item.recordingCount} replay${item.recordingCount === 1 ? '' : 's'} · ${item.status === 'raster' ? 'Basic texture only' : 'No map background'}. ` + (item.world ? 'Upload a full Workbench export or a quick archive-only texture package below.' : 'Check the Lobby2 recording metadata; a world identifier is needed before a map can be matched.');

        row.append(name, detail);

        if (item.world) {

          const upload = document.createElement('button'); upload.type = 'button'; upload.className = 'btn'; upload.textContent = 'Add map package';

          upload.onclick = () => { const target = document.getElementById('requestedMap'); target.hidden = false; target.dataset.world = item.world; target.textContent = `Map needed: ${item.world}`; form.scrollIntoView({block:'center'}); document.getElementById('mapPackage').focus(); };

          row.append(upload);

        }

        list.append(row);

      }

    } catch (error) { message.textContent = error.message; }

  }

  document.getElementById('refreshMissingMaps').onclick = missingMaps;

  const requested = new URLSearchParams(location.search).get('world');

  if (requested) { const target = document.getElementById('requestedMap'); target.hidden = false; target.dataset.world = requested; target.textContent = `Map needed: ${requested}`; }

  void missingMaps();

  async function list() {

    const response = await fetch('/api/admin/maps');

    if (!response.ok) throw new Error('Map administration is unavailable. Sign in with Developer access.');

    const maps = await response.json(), target = document.getElementById('mapList');

    target.replaceChildren();

    for (const item of maps) {

      const row = document.createElement('li');

      row.textContent = `${item.map.name} · Imported ${new Date(item.installedAt).toLocaleString()}`;

      if (item.canExport) {
        const link = document.createElement('a'); link.href = '/api/admin/maps/' + encodeURIComponent(item.id) + '/export';
        link.textContent = 'Download package'; link.style.marginLeft = '12px'; row.append(link);
      }
      if (item.map.quality === 'raster' || !item.map.details) row.prepend(document.createTextNode('Basic texture · '));
      target.append(row);

    }

    if (!maps.length) target.textContent = 'No uploaded packages yet. Bundled maps are still available.';

  }

  form.addEventListener('submit', async event => {

    event.preventDefault();

    const file = document.getElementById('mapPackage').files[0], button = form.querySelector('button');

    if (!file || file.size > 256 * 1024 * 1024) { status.textContent = 'Choose a map ZIP smaller than 256 MB.'; return; }

    button.disabled = true; progress.hidden = false; progress.value = 0;

    try {

      const response = await fetch('/api/auth/csrf');

      if (!response.ok) throw new Error('Please sign in again.');

      const {token} = await response.json();

      status.textContent = 'Uploading map…';

      await new Promise((resolve, reject) => {

        const xhr = new XMLHttpRequest();

        xhr.open('POST', '/api/admin/maps');

        xhr.setRequestHeader('X-CSRF-TOKEN', token);

        xhr.setRequestHeader('Content-Type', 'application/zip');

        xhr.upload.onprogress = e => { if (e.lengthComputable) progress.value = e.loaded / e.total * 100; };

        xhr.upload.onload = () => { status.textContent = 'Checking files and installing map…'; progress.removeAttribute('value'); };

        xhr.onerror = () => reject(new Error('Upload interrupted. The previous map remains available.'));

        xhr.onload = () => {

          if (xhr.status >= 200 && xhr.status < 300) resolve();

          else { let message = `Upload failed (${xhr.status}).`; try { message = JSON.parse(xhr.responseText).error || message; } catch {} reject(new Error(message)); }

        };

        xhr.send(file);

      });

      status.textContent = 'Map installed. Reload playback to use it.';

      form.reset(); await Promise.all([list(), missingMaps()]);

    } catch (error) { status.textContent = error.message; }

    finally { button.disabled = false; progress.hidden = true; }

  });

  list().catch(error => { status.textContent = error.message; });

})();

