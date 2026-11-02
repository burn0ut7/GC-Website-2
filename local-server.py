from functools import partial
from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler
from pathlib import Path
import webbrowser

root = Path(__file__).resolve().parent / 'dist'
handler = partial(SimpleHTTPRequestHandler, directory=str(root))
with ThreadingHTTPServer(('127.0.0.1', 0), handler) as server:
    url = f'http://127.0.0.1:{server.server_port}/#home'
    print(f'Local preview: {url}')
    print('Keep this window open. Close it to stop the preview.')
    webbrowser.open(url)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
