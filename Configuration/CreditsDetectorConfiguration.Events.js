define([], function () {
    'use strict';

    function bindTabNavigation(view) {
        const navButtons = view.querySelectorAll('.nav-button');
        const pages = {
            'settingsPage': view.querySelector('#settingsPage'),
            'notificationsPage': view.querySelector('#notificationsPage'),
            'ocrEnhancementsPage': view.querySelector('#ocrEnhancementsPage'),
            'actionsPage': view.querySelector('#actionsPage'),
            'rulesPage': view.querySelector('#rulesPage'),
            'autoSkipPage': view.querySelector('#autoSkipPage'),
            'guidePage': view.querySelector('#guidePage'),
            'apiPage': view.querySelector('#apiPage'),
            'tracerPage': view.querySelector('#tracerPage')
        };

        navButtons.forEach(btn => {
            btn.addEventListener('click', (e) => {
                e.preventDefault();
                const target = btn.getAttribute('data-target');

                navButtons.forEach(b => b.classList.remove('ui-btn-active'));
                btn.classList.add('ui-btn-active');

                Object.values(pages).forEach(page => {
                    if (page) page.classList.add('hide');
                });

                if (pages[target]) {
                    pages[target].classList.remove('hide');
                }
            });
        });
    }

    return {
        bindTabNavigation: bindTabNavigation
    };
});
