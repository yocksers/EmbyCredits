define([], function () {
    'use strict';

    async function loadPagePartials(view) {
        console.log('loadPagePartials called with view:', view);
        
        const parts = [
            { id: 'actionsPage', page: 'CreditsDetectorConfiguration.Actions' },
            { id: 'settingsPage', page: 'CreditsDetectorConfiguration.Settings' },
            { id: 'notificationsPage', page: 'CreditsDetectorConfiguration.Notifications' },
            { id: 'ocrEnhancementsPage', page: 'CreditsDetectorConfiguration.OcrEnhancements' },
            { id: 'guidePage', page: 'CreditsDetectorConfiguration.Guide' },
            { id: 'apiPage', page: 'CreditsDetectorConfiguration.API' }
        ];

        const promises = parts.map(p => {
            console.log('Fetching partial:', p.page);
            const cacheBuster = new Date().getTime();
            return fetch('/web/configurationpage?name=' + p.page + '&_=' + cacheBuster, {
                cache: 'no-cache',
                headers: {
                    'Cache-Control': 'no-cache, no-store, must-revalidate',
                    'Pragma': 'no-cache'
                }
            })
                .then(r => {
                    console.log('Fetch response for', p.page, '- Status:', r.status, 'OK:', r.ok);
                    if (!r.ok) throw new Error('Failed to load ' + p.page + ': ' + r.status);
                    return r.text();
                })
                .then(html => {
                    console.log('Got HTML for', p.page, '- Length:', html ? html.length : 0);
                    if (html) {
                        const el = view.querySelector('#' + p.id);
                        console.log('Found element for', p.id, ':', el);
                        if (el) {
                            el.innerHTML = html;
                            console.log('Set innerHTML for', p.id);
                        } else {
                            console.error('Could not find element with id: ' + p.id);
                        }
                    }
                });
        });

        await Promise.all(promises);
        console.log('All partials loaded');
        
        // Verify content loaded correctly - check for key elements
        setTimeout(() => {
            const actionsPage = view.querySelector('#actionsPage');
            if (actionsPage && actionsPage.innerHTML.trim().length === 0) {
                console.warn('Actions page appears empty - content may not have loaded correctly');
            }
        }, 100);
    }

    return {
        loadPagePartials: loadPagePartials
    };
});
