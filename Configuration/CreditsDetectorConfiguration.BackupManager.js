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
        fileInput.style.display = 'none';
        
        fileInput.addEventListener('change', function() {
            if (!fileInput.files || fileInput.files.length === 0) {
                return;
            }
            
            const file = fileInput.files[0];
            const reader = new FileReader();
            
            reader.onload = function(e) {
                try {
                    const jsonData = e.target.result;
                    
                    const backupData = JSON.parse(jsonData);
                    if (!backupData.Version || !backupData.Entries) {
                        throw new Error('Invalid backup file format');
                    }
                    
                    loading.show();
                    
                    const overwriteExisting = view.querySelector('#chkBackupImportOverwriteExisting')?.checked || false;
                    
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
                        loading.hide();
                        if (result.Success) {
                            const message = `Import complete! ${result.ItemsImported} imported, ${result.ItemsSkipped} skipped, ${result.ItemsNotFound} not found.`;
                            toast({ type: 'success', text: message });
                        } else {
                            toast({ type: 'error', text: result.Message || 'Import failed' });
                        }
                        document.body.removeChild(fileInput);
                    })
                    .catch(error => {
                        loading.hide();
                        console.error('Error importing backup:', error);
                        toast({ type: 'error', text: 'Failed to import backup: ' + error.message });
                        document.body.removeChild(fileInput);
                    });
                } catch (parseError) {
                    console.error('Error parsing backup file:', parseError);
                    toast({ type: 'error', text: 'Invalid backup file format: ' + parseError.message });
                    document.body.removeChild(fileInput);
                }
            };
            
            reader.onerror = function() {
                toast({ type: 'error', text: 'Failed to read backup file' });
                document.body.removeChild(fileInput);
            };
            
            reader.readAsText(file);
        });
        
        document.body.appendChild(fileInput);
        fileInput.click();
    }
    
    return {
        exportBackup: exportBackup,
        importBackup: importBackup
    };
});
