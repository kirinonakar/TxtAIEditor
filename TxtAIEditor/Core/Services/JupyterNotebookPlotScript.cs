namespace TxtAIEditor.Core.Services
{
    internal static class JupyterNotebookPlotScript
    {
        internal static string GetScript()
        {
            return @"    function initMplInteractiveContainers() {
        document.querySelectorAll('.cell-output').forEach(function(outputDiv) {
            outputDiv.querySelectorAll('img[src^=""data:image/png""]').forEach(function(img) {
                if (!img.closest('.mpl-interactive-wrapper')) {
                    const is3DImg = (img.getAttribute('data-is-3d') === 'true') || img.hasAttribute('data-elev');
                    const wrapper = document.createElement('div');
                    wrapper.className = 'mpl-interactive-wrapper';
                    wrapper.setAttribute('data-mpl', 'true');
                    wrapper.setAttribute('data-is-3d', is3DImg ? 'true' : 'false');
                    const statusText = is3DImg
                        ? 'Drag: Pan | 🔍 Zoom + Wheel: Zoom | Right-Click Drag: Rotate'
                        : 'Drag: Pan | 🔍 Zoom + Wheel: Zoom';
                    wrapper.innerHTML = 
                        '<div class=""mpl-toolbar"">' +
                            '<button class=""mpl-btn mpl-btn-reset"" title=""Reset View"">🔄 Reset</button>' +
                            '<button class=""mpl-btn mpl-btn-zoom"" title=""Toggle Zoom Mode (Scroll Wheel)"">🔍 Zoom</button>' +
                            '<button class=""mpl-btn mpl-btn-download"" title=""Download Image"">💾 Save PNG</button>' +
                            '<span class=""mpl-status-text"">' + statusText + '</span>' +
                        '</div>' +
                        '<div class=""mpl-viewport"">' +
                            '<div class=""mpl-plot-layer""></div>' +
                        '</div>';
                    img.parentNode.insertBefore(wrapper, img);
                    const plotLayer = wrapper.querySelector('.mpl-plot-layer');
                    img.className = 'mpl-plot-img';
                    plotLayer.appendChild(img);
                }
            });

            outputDiv.querySelectorAll('.mpl-interactive-wrapper').forEach(function(wrapper) {
                if (wrapper.__mplInited) return;
                wrapper.__mplInited = true;

                const viewport = wrapper.querySelector('.mpl-viewport');
                const plotLayer = wrapper.querySelector('.mpl-plot-layer');
                const btnReset = wrapper.querySelector('.mpl-btn-reset');
                const btnZoom = wrapper.querySelector('.mpl-btn-zoom');
                const btnDownload = wrapper.querySelector('.mpl-btn-download');
                const sliderY = wrapper.querySelector('.mpl-rotate-y-slider');
                const angleValY = wrapper.querySelector('.mpl-angle-val-y');
                const sliderX = wrapper.querySelector('.mpl-rotate-x-slider');
                const angleValX = wrapper.querySelector('.mpl-angle-val-x');

                let isZoomActive = false;

                const is3D = wrapper.getAttribute('data-is-3d') === 'true';
                const figId = wrapper.getAttribute('data-fig-id');
                let initElev = parseInt(wrapper.getAttribute('data-elev') || '30') || 30;
                let initAzim = parseInt(wrapper.getAttribute('data-azim') || '-60') || -60;
                let elev = initElev;
                let azim = initAzim;

                let panX = 0, panY = 0, scale = 1;
                let isDragging = false;
                let dragBtn = -1;
                let startX = 0, startY = 0;
                let startPanX = 0, startPanY = 0;
                let startElev = elev, startAzim = azim;

                let is3DInFlight = false;
                let pending3DElevAzim = null;

                /* 2D cumulative data state */
                let dataPanFracX = 0, dataPanFracY = 0, dataZoom = 1;
                let is2DInFlight = false;
                let pending2DState = null;

                function send3DViewRequest(eVal, aVal) {
                    if (!is3D || !figId) return;
                    if (is3DInFlight) {
                        pending3DElevAzim = { elev: eVal, azim: aVal };
                        return;
                    }
                    is3DInFlight = true;
                    try {
                        window.chrome.webview.postMessage(JSON.stringify({
                            type: 'updatePlotView',
                            figId: figId,
                            elev: eVal,
                            azim: aVal
                        }));
                    } catch (ex) {
                        is3DInFlight = false;
                    }
                }

                function send2DViewRequest(pfx, pfy, z) {
                    if (is3D || !figId) return;
                    if (is2DInFlight) {
                        pending2DState = { panFracX: pfx, panFracY: pfy, zoom: z };
                        return;
                    }
                    is2DInFlight = true;
                    try {
                        window.chrome.webview.postMessage(JSON.stringify({
                            type: 'update2DView',
                            figId: figId,
                            panFracX: pfx,
                            panFracY: pfy,
                            zoom: z
                        }));
                    } catch (ex) {
                        is2DInFlight = false;
                    }
                }

                let currentMouseX = 0, currentMouseY = 0;

                function setupPlotBounds() {
                    const rawBounds = wrapper.getAttribute('data-plot-bounds');
                    const clipDiv = wrapper.querySelector('.mpl-data-clip');
                    const imgWrapper = wrapper.querySelector('.mpl-data-img-wrapper');
                    const dataImg = wrapper.querySelector('.mpl-data-img');
                    const mainImg = wrapper.querySelector('.mpl-plot-img');

                    if (rawBounds && clipDiv && imgWrapper && mainImg) {
                        try {
                            const b = typeof rawBounds === 'string' ? JSON.parse(rawBounds) : rawBounds;
                            if (b && b.width > 0 && b.height > 0) {
                                clipDiv.style.display = 'block';
                                clipDiv.style.left = b.left + '%';
                                clipDiv.style.top = b.top + '%';
                                clipDiv.style.width = b.width + '%';
                                clipDiv.style.height = b.height + '%';

                                imgWrapper.style.left = (-b.left / b.width * 100) + '%';
                                imgWrapper.style.top = (-b.top / b.height * 100) + '%';

                                const wPx = mainImg.clientWidth;
                                const hPx = mainImg.clientHeight;
                                if (wPx > 0 && hPx > 0) {
                                    imgWrapper.style.width = (wPx / (b.width / 100)) + 'px';
                                    imgWrapper.style.height = (hPx / (b.height / 100)) + 'px';
                                    if (dataImg) {
                                        dataImg.style.width = wPx + 'px';
                                        dataImg.style.height = hPx + 'px';
                                        dataImg.style.maxWidth = 'none';
                                        dataImg.style.maxHeight = 'none';
                                    }
                                } else {
                                    imgWrapper.style.width = (100 / b.width * 100) + '%';
                                    imgWrapper.style.height = (100 / b.height * 100) + '%';
                                    if (dataImg) {
                                        dataImg.style.width = '100%';
                                        dataImg.style.height = '100%';
                                    }
                                }
                                return b;
                            }
                        } catch (ex) { }
                    }
                    if (clipDiv) {
                        clipDiv.style.display = 'none';
                    }
                    return null;
                }

                const mainImgRef = wrapper.querySelector('.mpl-plot-img');
                if (mainImgRef) {
                    mainImgRef.addEventListener('load', function() { setupPlotBounds(); });
                }
                setupPlotBounds();

                wrapper.__on3DUpdateReceived = function(html) {
                    const temp = document.createElement('div');
                    temp.innerHTML = html;
                    const newImg = temp.querySelector('.mpl-plot-img');
                    const oldImg = wrapper.querySelector('.mpl-plot-img');
                    if (newImg && oldImg) {
                        oldImg.src = newImg.src;
                    }
                    const newClipImg = temp.querySelector('.mpl-data-img');
                    const oldClipImg = wrapper.querySelector('.mpl-data-img');
                    if (newClipImg && oldClipImg) {
                        oldClipImg.src = newClipImg.src;
                    } else if (newImg && oldClipImg) {
                        oldClipImg.src = newImg.src;
                    }

                    const newWrapper = temp.querySelector('.mpl-interactive-wrapper');
                    if (newWrapper && newWrapper.hasAttribute('data-plot-bounds')) {
                        wrapper.setAttribute('data-plot-bounds', newWrapper.getAttribute('data-plot-bounds'));
                    }
                    setupPlotBounds();

                    const newCbar = temp.querySelector('.mpl-cbar-img');
                    const oldCbar = wrapper.querySelector('.mpl-cbar-img');
                    if (newCbar && oldCbar) {
                        oldCbar.src = newCbar.src;
                    }

                    if (is3D) {
                        if (isDragging) {
                            startX = currentMouseX;
                            startY = currentMouseY;
                            startElev = elev;
                            startAzim = azim;
                        }
                        is3DInFlight = false;
                        if (pending3DElevAzim) {
                            const next = pending3DElevAzim;
                            pending3DElevAzim = null;
                            send3DViewRequest(next.elev, next.azim);
                        }
                    } else {
                        /* 2D: reset CSS preview since new image has correct view */
                        if (isDragging) {
                            startX = currentMouseX;
                            startY = currentMouseY;
                            startPanX = 0;
                            startPanY = 0;
                        }
                        panX = 0;
                        panY = 0;
                        scale = 1;
                        updateTransform();
                        is2DInFlight = false;
                        if (pending2DState) {
                            const next = pending2DState;
                            pending2DState = null;
                            send2DViewRequest(next.panFracX, next.panFracY, next.zoom);
                        }
                    }
                };

                function updateTransform() {
                    const imgWrapper = wrapper.querySelector('.mpl-data-img-wrapper');
                    const hasBounds = wrapper.hasAttribute('data-plot-bounds') && wrapper.getAttribute('data-plot-bounds') !== '';
                    if (!is3D && imgWrapper && hasBounds) {
                        imgWrapper.style.transform = 'translate(' + panX + 'px, ' + panY + 'px)';
                    } else if (is3D && plotLayer) {
                        plotLayer.style.transform = 'translate(' + panX + 'px, ' + panY + 'px) scale(' + scale + ')';
                    }
                    if (is3D) {
                        if (sliderY) sliderY.value = azim;
                        if (angleValY) angleValY.textContent = 'Azim:' + azim + '°';
                        if (sliderX) sliderX.value = elev;
                        if (angleValX) angleValX.textContent = 'Elev:' + elev + '°';
                    }
                }

                if (viewport) {
                    viewport.addEventListener('mousedown', function(e) {
                        e.preventDefault();
                        isDragging = true;
                        dragBtn = e.button;
                        startX = e.clientX;
                        startY = e.clientY;
                        startPanX = panX;
                        startPanY = panY;
                        startElev = elev;
                        startAzim = azim;
                    });

                    window.addEventListener('mousemove', function(e) {
                        if (!isDragging) return;
                        currentMouseX = e.clientX;
                        currentMouseY = e.clientY;
                        const dx = e.clientX - startX;
                        const dy = e.clientY - startY;

                        if (is3D) {
                            if (dragBtn === 2) {
                                azim = Math.round(startAzim - dx * 0.5) % 360;
                                elev = Math.min(Math.max(-90, Math.round(startElev + dy * 0.5)), 90);
                                send3DViewRequest(elev, azim);
                            } else {
                                panX = startPanX + dx;
                                panY = startPanY + dy;
                            }
                        } else {
                            if (dragBtn === 0 || dragBtn === 1) {
                                panX = startPanX + dx;
                                panY = startPanY + dy;
                            }
                        }
                        updateTransform();
                    });

                    window.addEventListener('mouseup', function() {
                        if (isDragging) {
                            isDragging = false;
                            if (is3D && dragBtn === 2) {
                                send3DViewRequest(elev, azim);
                            } else if (!is3D && figId && (panX !== 0 || panY !== 0)) {
                                const clip = wrapper.querySelector('.mpl-data-clip');
                                const img = wrapper.querySelector('.mpl-plot-img');
                                const w = clip ? (clip.clientWidth || 600) : (img ? (img.clientWidth || 600) : 600);
                                const h = clip ? (clip.clientHeight || 400) : (img ? (img.clientHeight || 400) : 400);
                                dataPanFracX -= panX / w / dataZoom;
                                dataPanFracY += panY / h / dataZoom;
                                send2DViewRequest(dataPanFracX, dataPanFracY, dataZoom);
                            }
                        }
                    });

                    viewport.addEventListener('contextmenu', function(e) {
                        e.preventDefault();
                    });

                    viewport.addEventListener('wheel', function(e) {
                        if (!isZoomActive) return;
                        e.preventDefault();
                        const factor = e.deltaY < 0 ? 1.1 : 0.9;
                        scale = Math.min(Math.max(0.2, scale * factor), 5.0);
                        updateTransform();
                        if (!is3D && figId) {
                            dataZoom = Math.min(Math.max(0.2, dataZoom * factor), 5.0);
                            send2DViewRequest(dataPanFracX, dataPanFracY, dataZoom);
                        }
                    });
                }

                if (btnZoom) {
                    btnZoom.addEventListener('click', function() {
                        isZoomActive = !isZoomActive;
                        btnZoom.classList.toggle('active', isZoomActive);
                    });
                }

                if (sliderY) {
                    sliderY.addEventListener('input', function() {
                        if (is3D) {
                            azim = parseInt(sliderY.value) || 0;
                            send3DViewRequest(elev, azim);
                            updateTransform();
                        }
                    });
                }

                if (sliderX) {
                    sliderX.addEventListener('input', function() {
                        if (is3D) {
                            elev = parseInt(sliderX.value) || 0;
                            send3DViewRequest(elev, azim);
                            updateTransform();
                        }
                    });
                }

                if (btnReset) {
                    btnReset.addEventListener('click', function() {
                        panX = 0; panY = 0; scale = 1;
                        isZoomActive = false;
                        if (btnZoom) btnZoom.classList.remove('active');
                        if (is3D) {
                            elev = initElev;
                            azim = initAzim;
                            send3DViewRequest(elev, azim);
                        } else if (figId) {
                            dataPanFracX = 0;
                            dataPanFracY = 0;
                            dataZoom = 1;
                            send2DViewRequest(0, 0, 1);
                        }
                        updateTransform();
                    });
                }

                if (btnDownload) {
                    btnDownload.addEventListener('click', function() {
                        const mainImg = wrapper.querySelector('.mpl-plot-img');
                        const dataImg = wrapper.querySelector('.mpl-data-img');
                        const cbarImg = wrapper.querySelector('.mpl-cbar-img');
                        if (!mainImg) return;

                        const canvas = document.createElement('canvas');
                        const ctx = canvas.getContext('2d');
                        if (!ctx) return;

                        const w1 = mainImg.naturalWidth || mainImg.width || 600;
                        const h1 = mainImg.naturalHeight || mainImg.height || 400;
                        const w2 = cbarImg ? (cbarImg.naturalWidth || cbarImg.width || 100) : 0;
                        const h2 = cbarImg ? (cbarImg.naturalHeight || cbarImg.height || 400) : 0;

                        canvas.width = w1 + (w2 ? w2 + 20 : 0);
                        canvas.height = Math.max(h1, h2);

                        ctx.fillStyle = '#ffffff';
                        ctx.fillRect(0, 0, canvas.width, canvas.height);

                        ctx.drawImage(mainImg, 0, 0, w1, h1);
                        if (dataImg) {
                            ctx.drawImage(dataImg, 0, 0, w1, h1);
                        }
                        if (cbarImg && w2) {
                            ctx.drawImage(cbarImg, w1 + 20, 0, w2, h2);
                        }

                        const dataUrl = canvas.toDataURL('image/png');
                        window.__lastSavePlotBtn = btnDownload;
                        if (!btnDownload.getAttribute('data-orig-text')) {
                            btnDownload.setAttribute('data-orig-text', btnDownload.textContent);
                        }
                        btnDownload.textContent = 'Saving...';

                        try {
                            if (window.chrome && window.chrome.webview) {
                                window.chrome.webview.postMessage(JSON.stringify({
                                    type: 'savePlotImage',
                                    imageData: dataUrl
                                }));
                                return;
                            }
                        } catch (e) {}

                        const a = document.createElement('a');
                        a.download = 'matplotlib_plot.png';
                        a.href = dataUrl;
                        a.click();
                        btnDownload.textContent = 'Saved!';
                        setTimeout(() => { btnDownload.textContent = '💾 Save PNG'; }, 2000);
                    });
                }
            });
        });
    }

    function renderAllMarkdownCells() {
        container.querySelectorAll('.cell[data-cell-type=""markdown""]').forEach(function(cellDiv) {
            renderMarkdownCell(cellDiv);
        });
    }

    renderAllMarkdownCells();
    if (typeof katex === 'undefined') {
        try {
            const link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = 'https://cdn.jsdelivr.net/npm/katex@0.16.8/dist/katex.min.css';
            document.head.appendChild(link);
            const script = document.createElement('script');
            script.src = 'https://cdn.jsdelivr.net/npm/katex@0.16.8/dist/katex.min.js';
            script.onload = function() { renderAllMarkdownCells(); };
            document.head.appendChild(script);
        } catch(ex) {}
    } else {
        setTimeout(renderAllMarkdownCells, 100);
    }

    setTimeout(initMplInteractiveContainers, 300);
    applyAllCodeCellsHighlight();
})();";
        }
    }
}
