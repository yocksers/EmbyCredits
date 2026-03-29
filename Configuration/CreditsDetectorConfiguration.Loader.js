define([], function () {
    'use strict';

    async function loadPagePartials(view) {
        const parts = [
            { id: 'actionsPage', page: 'CreditsDetectorConfiguration.Actions' },
            { id: 'settingsPage', page: 'CreditsDetectorConfiguration.Settings' },
            { id: 'rulesPage', page: 'CreditsDetectorConfiguration.Rules' },
            { id: 'notificationsPage', page: 'CreditsDetectorConfiguration.Notifications' },
            { id: 'ocrEnhancementsPage', page: 'CreditsDetectorConfiguration.OcrEnhancements' },
            { id: 'autoSkipPage', page: 'CreditsDetectorConfiguration.AutoSkip' },
            { id: 'guidePage', page: 'CreditsDetectorConfiguration.Guide' },
            { id: 'apiPage', page: 'CreditsDetectorConfiguration.API' },
            { id: 'chapterEditPage', page: 'CreditsDetectorConfiguration.ChapterEdit' },
            { id: 'tracerPage',      page: 'CreditsDetectorConfiguration.Tracer' }
        ];

        const promises = parts.map(p => {
            const cacheBuster = new Date().getTime();
            return fetch(ApiClient.getUrl('web/configurationpage', { name: p.page, _: cacheBuster }), {
                cache: 'no-cache',
                headers: {
                    'Cache-Control': 'no-cache, no-store, must-revalidate',
                    'Pragma': 'no-cache'
                }
            })
                .then(r => {
                    if (!r.ok) throw new Error('Failed to load ' + p.page + ': ' + r.status);
                    return r.text();
                })
                .then(html => {
                    if (html) {
                        const el = view.querySelector('#' + p.id);
                        if (el) {
                            el.innerHTML = html;
                        } else {
                            console.error('Could not find element with id: ' + p.id);
                        }
                    }
                });
        });

        await Promise.all(promises);
        
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
