"""Keep malformed Crossref titles out of the pinned upstream result parser."""

from searx.engines import crossref as _upstream

# Preserve upstream request generation, attribution and scientific categories.
about = dict(_upstream.about)
categories = list(_upstream.categories)
paging = _upstream.paging
search_url = _upstream.search_url
request = _upstream.request


def _first_text(values):
    if not isinstance(values, list):
        return None
    return next((value.strip() for value in values if isinstance(value, str) and value.strip()), None)


class _Response:
    def __init__(self, original, data):
        self._original = original
        self._data = data

    def json(self):
        return self._data

    def __getattr__(self, name):
        return getattr(self._original, name)


def response(resp):
    data = resp.json()
    message = data.get("message") if isinstance(data, dict) else None
    records = message.get("items") if isinstance(message, dict) else None
    safe_records = []
    for record in records if isinstance(records, list) else []:
        if not isinstance(record, dict):
            continue
        title = _first_text(record.get("title"))
        if title is None:
            continue
        # Upstream indexes both fields and normalizes title with a regex. Empty
        # arrays, JSON null and non-string values must not discard healthy works.
        journal = _first_text(record.get("container-title")) or ""
        if record.get("type") == "book-chapter" and not journal:
            journal = title
        safe_records.append({**record, "title": [title], "container-title": [journal]})
    payload = {**(data if isinstance(data, dict) else {}), "message": {
        **(message if isinstance(message, dict) else {}), "items": safe_records}}
    return _upstream.response(_Response(resp, payload))
