(function () {
    'use strict';

    function addCreditsDetectorToSidebar() {
        var checkInterval = setInterval(function() {
            var drawerContent = document.querySelector('.mainDrawerContent') || 
                              document.querySelector('.sidebarNav') ||
                              document.querySelector('.drawerContent');
            
            if (!drawerContent) {
                return;
            }

            var existingLink = document.querySelector('a[data-credits-detector-menu="true"]');
            if (existingLink) {
                clearInterval(checkInterval);
                return;
            }

            var pluginLinks = drawerContent.querySelectorAll('a[href*="configurationpage"]');
            var insertPoint = null;

            for (var i = 0; i < pluginLinks.length; i++) {
                var href = pluginLinks[i].getAttribute('href') || '';
                if (href.indexOf('Plugins') !== -1 || href.indexOf('plugins') !== -1) {
                    insertPoint = pluginLinks[i].parentElement;
                    break;
                }
            }

            if (!insertPoint && drawerContent.children.length > 0) {
                insertPoint = drawerContent;
            }

            if (insertPoint) {
                var menuItem = document.createElement('a');
                menuItem.className = 'navMenuOption lnkMediaFolder emby-button';
                menuItem.href = 'configurationpage?name=CreditsDetectorConfiguration';
                menuItem.setAttribute('data-credits-detector-menu', 'true');
                menuItem.setAttribute('is', 'emby-linkbutton');
                
                var icon = document.createElement('i');
                icon.className = 'md-icon navMenuOptionIcon';
                icon.textContent = 'movie_filter';
                
                var text = document.createElement('span');
                text.className = 'navMenuOptionText';
                text.textContent = 'Credits Detector';
                
                menuItem.appendChild(icon);
                menuItem.appendChild(text);
                
                if (insertPoint === drawerContent) {
                    drawerContent.appendChild(menuItem);
                } else {
                    insertPoint.parentNode.insertBefore(menuItem, insertPoint.nextSibling);
                }
                
                clearInterval(checkInterval);
            }
        }, 500);

        setTimeout(function() {
            clearInterval(checkInterval);
        }, 30000);
    }

    if (window.Dashboard) {
        addCreditsDetectorToSidebar();
    } else {
        document.addEventListener('DOMContentLoaded', addCreditsDetectorToSidebar);
    }

    if (window.ApiClient) {
        Events.on(ApiClient, 'websocketmessage', function(e, msg) {
            if (msg.MessageType === 'UserConfigurationUpdated') {
                setTimeout(addCreditsDetectorToSidebar, 1000);
            }
        });
    }
})();
