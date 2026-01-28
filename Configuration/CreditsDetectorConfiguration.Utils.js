define([], function () {
    'use strict';

    const pluginId = "b1a65a73-a620-432a-9f5b-285038031c26";

    function getPluginConfiguration() {
        return ApiClient.getPluginConfiguration(pluginId);
    }

    function updatePluginConfiguration(config) {
        return ApiClient.updatePluginConfiguration(pluginId, config);
    }

    function initializeCollapsibleSections(view) {
        const headers = view.querySelectorAll('.collapsible-header');
        headers.forEach(header => {
            header.addEventListener('click', function() {
                const content = this.nextElementSibling;
                const icon = this.querySelector('.collapse-icon');
                
                if (content && content.classList.contains('collapsible-content')) {
                    const isHidden = content.style.display === 'none';
                    content.style.display = isHidden ? 'block' : 'none';
                    
                    if (icon) {
                        icon.style.transform = isHidden ? 'rotate(180deg)' : 'rotate(0deg)';
                    }
                }
            });
        });
    }

    return {
        getPluginConfiguration: getPluginConfiguration,
        updatePluginConfiguration: updatePluginConfiguration,
        pluginId: pluginId,
        initializeCollapsibleSections: initializeCollapsibleSections
    };
});
