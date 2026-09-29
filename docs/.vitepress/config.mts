import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Integrios',
  description: 'Integrios documentation',
  srcExclude: ['**/*.local.md'],
  ignoreDeadLinks: [/^http:\/\/localhost:\d+\/?$/],
  themeConfig: {
    search: { provider: 'local' },
    nav: [
      { text: 'Home', link: '/' },
      { text: 'Getting started', link: '/setup' },
      { text: 'Concepts', link: '/architecture' },
      { text: 'Configuration', link: '/configuration' },
      { text: 'Operations', link: '/observability' },
      { text: 'Deployment', link: '/database-backends' }
    ],
    sidebar: [
      { text: 'Getting started', items: [
        { text: 'Setup', link: '/setup' },
        { text: 'GitHub to Slack walkthrough', link: '/github-to-slack-walkthrough' }
      ] },
      { text: 'Concepts and architecture', items: [
        { text: 'Architecture', link: '/architecture' },
        { text: 'Connector manifests', link: '/connector-manifest' }
      ] },
      { text: 'Configuration', items: [
        { text: 'Runtime configuration', link: '/configuration' },
        { text: 'Operator dashboard', link: '/operator-dashboard' }
      ] },
      { text: 'Operations', items: [
        { text: 'Observability', link: '/observability' },
        { text: 'CI and releases', link: '/ci-cd' }
      ] },
      { text: 'Deployment', items: [
        { text: 'Database backends', link: '/database-backends' },
        { text: 'Deployment guide', link: 'https://github.com/arminzou/integrios/blob/main/deploy/README.md' }
      ] }
    ]
  }
})
