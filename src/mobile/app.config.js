// Web base path: '' (root) for the on-prem nginx gateway, '/Tao' for GitHub Pages.
module.exports = ({ config }) => ({
  ...config,
  experiments: { ...config.experiments, baseUrl: process.env.TAO_WEB_BASE_PATH ?? '' }
});
