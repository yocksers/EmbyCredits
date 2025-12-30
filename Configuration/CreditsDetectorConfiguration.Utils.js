define([], function () {
    'use strict';

    const pluginId = "b1a65a73-a620-432a-9f5b-285038031c26";

    function getPluginConfiguration() {
        return ApiClient.getPluginConfiguration(pluginId);
    }

    function updatePluginConfiguration(config) {
        return ApiClient.updatePluginConfiguration(pluginId, config);
    }

    return {
        getPluginConfiguration: getPluginConfiguration,
        updatePluginConfiguration: updatePluginConfiguration,
        pluginId: pluginId
    };
});
