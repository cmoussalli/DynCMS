// DynCMS analytics map: Leaflet (loaded on demand from cdnjs) with one bubble per city, sized by page views.
const LEAFLET_VERSION = '1.9.4';
const LEAFLET_JS = `https://cdnjs.cloudflare.com/ajax/libs/leaflet/${LEAFLET_VERSION}/leaflet.min.js`;
const LEAFLET_CSS = `https://cdnjs.cloudflare.com/ajax/libs/leaflet/${LEAFLET_VERSION}/leaflet.min.css`;

let leafletLoading = null;

function loadLeaflet() {
    if (window.L && window.L.map) return Promise.resolve(true);
    if (leafletLoading) return leafletLoading;
    leafletLoading = new Promise(resolve => {
        if (!document.querySelector('link[data-dc-leaflet]')) {
            const link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = LEAFLET_CSS;
            link.setAttribute('data-dc-leaflet', '');
            document.head.appendChild(link);
        }
        const script = document.createElement('script');
        script.src = LEAFLET_JS;
        script.async = true;
        script.onload = () => resolve(!!(window.L && window.L.map));
        script.onerror = () => { leafletLoading = null; resolve(false); };
        document.head.appendChild(script);
    });
    return leafletLoading;
}

function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export async function init(element, tileUrl, attribution) {
    if (!element) return false;
    const ok = await loadLeaflet();
    if (!ok) return false;
    if (element.__dcMap) return true;

    const map = L.map(element, { worldCopyJump: true, minZoom: 1, zoomControl: true, attributionControl: true });
    map.setView([24, 12], 2);
    L.tileLayer(tileUrl, { attribution: attribution, maxZoom: 18 }).addTo(map);
    const layer = L.layerGroup().addTo(map);
    element.__dcMap = { map, layer, fitted: false };
    setTimeout(() => map.invalidateSize(), 60);
    return true;
}

export function setPoints(element, points, fit) {
    const state = element && element.__dcMap;
    if (!state) return;
    state.layer.clearLayers();
    if (!points || points.length === 0) return;

    const max = Math.max(1, ...points.map(p => p.views));
    const sorted = [...points].sort((a, b) => b.views - a.views);
    for (const p of sorted) {
        const radius = 5 + 24 * Math.sqrt(p.views / max);
        const marker = L.circleMarker([p.lat, p.lon], {
            radius: radius,
            color: '#4f5bd5',
            weight: 1.5,
            fillColor: '#4f5bd5',
            fillOpacity: 0.32
        });
        marker.bindPopup(
            `<strong>${escapeHtml(p.label)}</strong><br>` +
            `${p.views.toLocaleString()} page view${p.views === 1 ? '' : 's'} · ${p.visitors.toLocaleString()} visitor${p.visitors === 1 ? '' : 's'}`);
        marker.bindTooltip(`${escapeHtml(p.label)}: ${p.views.toLocaleString()}`, { direction: 'top', offset: [0, -radius] });
        marker.addTo(state.layer);
    }

    if (fit || !state.fitted) {
        const bounds = L.latLngBounds(sorted.map(p => [p.lat, p.lon]));
        state.map.fitBounds(bounds.pad(0.25), { maxZoom: 6 });
        state.fitted = true;
    }
}

export function focus(element, lat, lon, zoom) {
    const state = element && element.__dcMap;
    if (!state) return;
    state.map.flyTo([lat, lon], zoom || 8);
}

export function dispose(element) {
    const state = element && element.__dcMap;
    if (!state) return;
    state.map.remove();
    delete element.__dcMap;
}
