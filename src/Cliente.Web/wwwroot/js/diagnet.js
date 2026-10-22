// Puente mínimo entre Blazor y el navegador (script clásico: expone el objeto global DiagNET; no es un módulo para no depender del mapa de importación con huellas). Solo hace lo que Blazor no puede: almacenamiento local, portapapeles y manejo de foco del panel de detalle.
// No contiene lógica de negocio ni datos.

let retornoFoco = null;
let panelActivo = null;
let manejadorTab = null;

function almacenamientoDisponible() {
    try {
        const clave = '__diagnet_prueba__';
        window.localStorage.setItem(clave, '1');
        window.localStorage.removeItem(clave);
        return true;
    } catch (e) {
        return false;
    }
}

// Estas funciones lanzan si el almacenamiento no está disponible: el llamador lo trata como «no disponible».
function leer(clave) {
    return window.localStorage.getItem(clave);
}

function escribir(clave, valor) {
    window.localStorage.setItem(clave, valor);
}

async function copiar(texto) {
    try {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(texto);
            return true;
        }
    } catch (e) { /* se prueba el método alterno */ }

    try {
        const area = document.createElement('textarea');
        area.value = texto;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();
        const ok = document.execCommand('copy');
        document.body.removeChild(area);
        return ok;
    } catch (e) {
        return false;
    }
}

// Entrega un archivo recibido de la API como descarga del navegador. Devuelve false si el navegador no pudo iniciarla.
function descargar(nombre, contenido, tipo) {
    try {
        const url = URL.createObjectURL(new Blob([contenido], { type: tipo }));
        const enlace = document.createElement('a');
        enlace.href = url;
        enlace.download = nombre;
        enlace.style.display = 'none';
        document.body.appendChild(enlace);
        enlace.click();
        document.body.removeChild(enlace);
        setTimeout(() => URL.revokeObjectURL(url), 30000);
        return true;
    } catch (e) {
        return false;
    }
}

function enfocar(elemento) {
    if (elemento instanceof Node && document.contains(elemento)) {
        elemento.focus({ preventScroll: false });
        return true;
    }
    return false;
}

function direccionActual() {
    return window.location.href;
}

function enfocables(panel) {
    return Array.from(panel.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]), summary, [tabindex]:not([tabindex="-1"])'))
        .filter(e => e.offsetParent !== null || e === document.activeElement);
}

// Abre el panel modal: recuerda el elemento que lo abrió (selector del disparador), mueve el foco al panel, lo mantiene dentro (Tab/Mayús+Tab), cierra con Escape
// (aunque el foco se haya perdido) y bloquea el desplazamiento de la página.
function abrirPanel(panel, selectorDisparador, puente) {
    // El disparador se identifica con un selector (data-indice): una referencia de elemento de Blazor no sobrevive al paso entre componentes.
    const elegido = selectorDisparador ? document.querySelector(selectorDisparador) : null;
    retornoFoco = elegido || document.activeElement;
    panelActivo = panel;
    document.body.classList.add('panel-abierto');

    manejadorTab = (evento) => {
        if (!panelActivo) return;
        if (evento.key === 'Escape') {
            evento.preventDefault();
            evento.stopPropagation();
            if (puente) puente.invokeMethodAsync('Cerrar');
            return;
        }
        if (evento.key !== 'Tab') return;
        const lista = enfocables(panelActivo);
        if (lista.length === 0) {
            evento.preventDefault();
            return;
        }
        const primero = lista[0];
        const ultimo = lista[lista.length - 1];
        if (evento.shiftKey && (document.activeElement === primero || !panelActivo.contains(document.activeElement))) {
            evento.preventDefault();
            ultimo.focus();
        } else if (!evento.shiftKey && (document.activeElement === ultimo || !panelActivo.contains(document.activeElement))) {
            evento.preventDefault();
            primero.focus();
        }
    };
    document.addEventListener('keydown', manejadorTab, true);

    const lista = enfocables(panel);
    if (lista.length > 0) lista[0].focus();
}

// Cierra el panel y devuelve el foco al elemento que lo abrió (si sigue en la página).
function cerrarPanel() {
    if (manejadorTab) document.removeEventListener('keydown', manejadorTab, true);
    manejadorTab = null;
    panelActivo = null;
    document.body.classList.remove('panel-abierto');
    const destino = retornoFoco;
    retornoFoco = null;
    if (destino instanceof Node && document.contains(destino)) destino.focus();
}

window.DiagNET = { almacenamientoDisponible, leer, escribir, copiar, descargar, enfocar, direccionActual, abrirPanel, cerrarPanel };
