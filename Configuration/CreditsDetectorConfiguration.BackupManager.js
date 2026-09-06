define(['loading', 'toast'], function (loading, toast) {
    'use strict';
    
    function exportBackup(view) {
        loading.show();
        
        const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, -5);
        const filename = `credits-backup-${timestamp}.json`;
        
        const url = ApiClient.getUrl('CreditsDetector/ExportCreditsBackup', {
            'X-Emby-Token': ApiClient.accessToken()
        });
        
        fetch(url, {
            method: 'POST',
            headers: {
                'X-Emby-Token': ApiClient.accessToken(),
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                LibraryIds: null,
                SeriesIds: null
            })
        })
        .then(response => {
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}: ${response.statusText}`);
            }
            return response.blob();
        })
        .then(blob => {
            loading.hide();
            
            const url = window.URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.style.display = 'none';
            a.href = url;
            a.download = filename;
            document.body.appendChild(a);
            a.click();
            window.URL.revokeObjectURL(url);
            document.body.removeChild(a);
            
            toast({ type: 'success', text: 'Backup exported successfully' });
        })
        .catch(error => {
            loading.hide();
            console.error('Error exporting backup:', error);
            toast({ type: 'error', text: 'Failed to export backup: ' + error.message });
        });
    }
    
    function importBackup(view) {
        const fileInput = document.createElement('input');
        fileInput.type = 'file';
        fileInput.accept = '.json';
        fileInput.multiple = true;
        fileInput.style.display = 'none';
        
        fileInput.addEventListener('change', function() {
            if (!fileInput.files || fileInput.files.length === 0) {
                return;
            }
            
            const files = Array.from(fileInput.files);
            const overwriteExisting = view.querySelector('#chkBackupImportOverwriteExisting')?.checked || false;
            
            loading.show();
            
            let totalImported = 0;
            let totalSkipped = 0;
            let totalNotFound = 0;
            let filesProcessed = 0;
            let filesSucceeded = 0;
            let filesFailed = 0;
            
            const processFile = (file) => {
                return new Promise((resolve, reject) => {
                    const reader = new FileReader();
                    
                    reader.onload = function(e) {
                        try {
                            const jsonData = e.target.result;
                            const backupData = JSON.parse(jsonData);
                            
                            if (!backupData.Version || !backupData.Entries) {
                                reject(new Error(`${file.name}: Invalid backup file format`));
                                return;
                            }
                            
                            const url = ApiClient.getUrl('CreditsDetector/ImportCreditsBackup');
                            fetch(url, {
                                method: 'POST',
                                headers: {
                                    'X-Emby-Token': ApiClient.accessToken(),
                                    'Content-Type': 'application/json'
                                },
                                body: JSON.stringify({
                                    JsonData: jsonData,
                                    OverwriteExisting: overwriteExisting
                                })
                            })
                            .then(response => {
                                if (!response.ok) {
                                    throw new Error(`HTTP ${response.status}: ${response.statusText}`);
                                }
                                return response.json();
                            })
                            .then(result => {
                                if (result.Success) {
                                    totalImported += result.ItemsImported || 0;
                                    totalSkipped += result.ItemsSkipped || 0;
                                    totalNotFound += result.ItemsNotFound || 0;
                                    filesSucceeded++;
                                    resolve();
                                } else {
                                    reject(new Error(`${file.name}: ${result.Message || 'Import failed'}`));
                                }
                            })
                            .catch(error => {
                                reject(new Error(`${file.name}: ${error.message}`));
                            });
                        } catch (parseError) {
                            reject(new Error(`${file.name}: Invalid JSON format - ${parseError.message}`));
                        }
                    };
                    
                    reader.onerror = function() {
                        reject(new Error(`${file.name}: Failed to read file`));
                    };
                    
                    reader.readAsText(file);
                });
            };
            
            Promise.allSettled(files.map(file => processFile(file)))
                .then(results => {
                    loading.hide();
                    
                    filesProcessed = results.length;
                    filesFailed = results.filter(r => r.status === 'rejected').length;
                    
                    const errors = results
                        .filter(r => r.status === 'rejected')
                        .map(r => r.reason.message);
                    
                    if (filesFailed === 0) {
                        const message = files.length === 1
                            ? `Import complete! ${totalImported} imported, ${totalSkipped} skipped, ${totalNotFound} not found.`
                            : `All ${filesSucceeded} file(s) imported successfully! Total: ${totalImported} imported, ${totalSkipped} skipped, ${totalNotFound} not found.`;
                        toast({ type: 'success', text: message });
                    } else if (filesSucceeded === 0) {
                        toast({ type: 'error', text: `All ${filesFailed} file(s) failed to import. Check console for details.` });
                        console.error('Import errors:', errors);
                    } else {
                        toast({ type: 'warning', text: `Partial success: ${filesSucceeded} succeeded, ${filesFailed} failed. Total: ${totalImported} imported, ${totalSkipped} skipped, ${totalNotFound} not found.` });
                        console.warn('Import errors:', errors);
                    }
                    
                    document.body.removeChild(fileInput);
                })
                .catch(error => {
                    loading.hide();
                    console.error('Unexpected error during import:', error);
                    toast({ type: 'error', text: 'Unexpected error during import' });
                    document.body.removeChild(fileInput);
                });
        });
        
        document.body.appendChild(fileInput);
        fileInput.click();
    }
    
    let bulkExportSeriesData = [];
    
    function openBulkExportModal(view) {
        const modal = view.querySelector('#bulkExportModal');
        if (!modal) return;

        const backupFolder = view.querySelector('#txtBackupFolderPath')?.value?.trim();
        if (!backupFolder) {
            toast({ type: 'error', text: 'Backup Folder Path is not configured. Please set it in Settings before using bulk export.' });
            return;
        }

        modal.style.display = 'flex';
        loadBulkExportLibraries(view);
        loadBulkExportSeriesList(view, '');
    }
    
    function closeBulkExportModal(view) {
        const modal = view.querySelector('#bulkExportModal');
        if (!modal) return;
        modal.style.display = 'none';
        bulkExportSeriesData = [];
    }
    
    function loadBulkExportLibraries(view) {
        ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders')).then(response => {
            const selectLibrary = view.querySelector('#selectBulkExportLibrary');
            if (!selectLibrary) return;
            
            selectLibrary.innerHTML = '<option value="">-- All Libraries --</option>';

            const tvLibraries = response.Items.filter(library => {
                return library.CollectionType === 'tvshows' || library.CollectionType === 'mixed' || !library.CollectionType;
            });

            tvLibraries.sort((a, b) => a.Name.localeCompare(b.Name));

            tvLibraries.forEach(library => {
                const option = document.createElement('option');
                option.value = library.Id;
                option.textContent = library.Name;
                selectLibrary.appendChild(option);
            });
        }).catch(error => {
            console.error('Error loading libraries:', error);
        });
    }
    
    function loadBulkExportSeriesList(view, libraryId) {
        const seriesList = view.querySelector('#bulkExportSeriesList');
        if (!seriesList) return;
        
        seriesList.innerHTML = '<div style="text-align: center; opacity: 0.6;">Loading TV shows...</div>';

        let url = ApiClient.getUrl('CreditsDetector/GetAllSeries');
        if (libraryId) {
            url = ApiClient.getUrl('CreditsDetector/GetAllSeries', { LibraryId: libraryId });
        }

        ApiClient.getJSON(url).then(response => {
            const series = response.Series || [];
            series.sort((a, b) => a.Name.localeCompare(b.Name));
            
            bulkExportSeriesData = series;
            
            if (series.length === 0) {
                seriesList.innerHTML = '<div style="text-align: center; opacity: 0.6; padding: 1em;">No TV shows found</div>';
                return;
            }
            
            seriesList.innerHTML = '';
            series.forEach(s => {
                const div = document.createElement('div');
                div.className = 'checkboxContainer';
                div.style.marginBottom = '0.5em';
                div.innerHTML = `
                    <label style="display: flex; align-items: center;">
                        <input is="emby-checkbox" type="checkbox" class="chkBulkExportSeries" data-series-id="${s.Id}" data-series-name="${s.Name.replace(/"/g, '&quot;')}" />
                        <span style="margin-left: 0.5em;">${s.Name}</span>
                    </label>
                `;
                seriesList.appendChild(div);
            });
        }).catch(error => {
            console.error('Error loading series:', error);
            seriesList.innerHTML = '<div style="text-align: center; padding: 1em;">Failed to load TV shows</div>';
        });
    }
    
    function selectAllSeries(view) {
        const checkboxes = view.querySelectorAll('.chkBulkExportSeries');
        checkboxes.forEach(cb => cb.checked = true);
    }
    
    function deselectAllSeries(view) {
        const checkboxes = view.querySelectorAll('.chkBulkExportSeries');
        checkboxes.forEach(cb => cb.checked = false);
    }
    
    function confirmBulkExport(view) {
        const checkboxes = view.querySelectorAll('.chkBulkExportSeries:checked');
        
        if (checkboxes.length === 0) {
            require(['toast'], function(toast) {
                toast({ type: 'warning', text: 'Please select at least one TV show to export' });
            });
            return;
        }
        
        const selectedSeriesIds = Array.from(checkboxes).map(cb => cb.getAttribute('data-series-id'));
        const selectedCount = selectedSeriesIds.length;
        
        closeBulkExportModal(view);
        
        loading.show();
        
        const url = ApiClient.getUrl('CreditsDetector/BulkExportToFolder');
        fetch(url, {
            method: 'POST',
            headers: {
                'X-Emby-Token': ApiClient.accessToken(),
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ SeriesIds: selectedSeriesIds })
        })
        .then(response => {
            if (!response.ok) throw new Error('HTTP ' + response.status);
            return response.json();
        })
        .then(result => {
            loading.hide();
            if (result.Success) {
                toast({ type: 'success', text: `Exported ${selectedCount} series (${result.EpisodesWithCredits} episodes) to: ${result.FolderPath}` });
            } else {
                toast({ type: 'error', text: 'Export failed: ' + result.Message });
            }
        })
        .catch(error => {
            loading.hide();
            console.error('Error during bulk export:', error);
            toast({ type: 'error', text: 'Bulk export failed: ' + error.message });
        });
    }
    
    function clearZeroCreditsMarkers(view) {
        if (!confirm('Scan the entire library and remove end credits markers saved at 0:00?\n\nThis only affects credits markers with an invalid 0:00 timestamp — chapters, intro markers, and valid credits markers are not touched.')) {
            return;
        }

        const button = view.querySelector('#btnClearZeroCreditsMarkers');
        const originalLabel = button ? button.querySelector('span').textContent : '';
        if (button) button.disabled = true;

        const startUrl = ApiClient.getUrl('CreditsDetector/ClearZeroCreditsMarkers');

        fetch(startUrl, {
            method: 'POST',
            headers: {
                'X-Emby-Token': ApiClient.accessToken(),
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({})
        })
        .then(response => {
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}: ${response.statusText}`);
            }
            return response.json();
        })
        .then(result => {
            if (!result.Success) {
                if (button) {
                    button.disabled = false;
                    button.querySelector('span').textContent = originalLabel;
                }
                toast({ type: 'error', text: 'Failed to start: ' + result.Message });
                return;
            }

            pollClearZeroCreditsProgress(button, originalLabel);
        })
        .catch(error => {
            if (button) {
                button.disabled = false;
                button.querySelector('span').textContent = originalLabel;
            }
            console.error('Error starting clear invalid credits markers:', error);
            toast({ type: 'error', text: 'Failed to clear markers: ' + error.message });
        });
    }

    function pollClearZeroCreditsProgress(button, originalLabel) {
        const progressUrl = ApiClient.getUrl('CreditsDetector/GetClearZeroCreditsProgress');

        const poll = () => {
            fetch(progressUrl, {
                headers: { 'X-Emby-Token': ApiClient.accessToken() }
            })
            .then(response => {
                if (!response.ok) throw new Error('HTTP ' + response.status);
                return response.json();
            })
            .then(progress => {
                if (progress.IsRunning) {
                    if (button) {
                        button.querySelector('span').textContent = `Clearing... ${progress.ProcessedItems}/${progress.TotalItems}`;
                    }
                    setTimeout(poll, 1000);
                    return;
                }

                if (button) {
                    button.disabled = false;
                    button.querySelector('span').textContent = originalLabel;
                }

                if (progress.Success) {
                    toast({ type: 'success', text: `Cleared ${progress.ClearedCount} invalid credits marker(s)` });
                } else {
                    toast({ type: 'error', text: 'Failed to clear markers: ' + (progress.CurrentItem || 'Unknown error') });
                }
            })
            .catch(error => {
                if (button) {
                    button.disabled = false;
                    button.querySelector('span').textContent = originalLabel;
                }
                console.error('Error polling clear invalid credits markers progress:', error);
                toast({ type: 'error', text: 'Lost progress updates: ' + error.message });
            });
        };

        poll();
    }

    return {
        exportBackup: exportBackup,
        importBackup: importBackup,
        openBulkExportModal: openBulkExportModal,
        closeBulkExportModal: closeBulkExportModal,
        loadBulkExportSeriesList: loadBulkExportSeriesList,
        selectAllSeries: selectAllSeries,
        deselectAllSeries: deselectAllSeries,
        confirmBulkExport: confirmBulkExport,
        clearZeroCreditsMarkers: clearZeroCreditsMarkers
    };
});
