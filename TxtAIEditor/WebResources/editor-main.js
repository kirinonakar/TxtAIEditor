const moduleVersion = window.__TxtAIEditorVersion || Date.now();
await import(`./editor-ui.js?v=${encodeURIComponent(moduleVersion)}`);
