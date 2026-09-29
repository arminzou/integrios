import DefaultTheme from 'vitepress/theme'
import './custom.css'

export default {
  ...DefaultTheme,
  enhanceApp() {
    if (typeof window === 'undefined') return

    let hideScrollbar: number
    window.addEventListener('scroll', (event) => {
      const sidebar = event.target
      if (!(sidebar instanceof HTMLElement) || !sidebar.classList.contains('VPSidebar')) return

      sidebar.classList.add('is-scrolling')
      window.clearTimeout(hideScrollbar)
      hideScrollbar = window.setTimeout(() => sidebar.classList.remove('is-scrolling'), 800)
    }, true)
  }
}
