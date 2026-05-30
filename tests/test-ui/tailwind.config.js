/** @type {import('tailwindcss').Config} */
export default {
  content: [
    './index.html',
    './src/**/*.{vue,js,ts,jsx,tsx}'
  ],
  theme: {
    extend: {
      fontFamily: {
        sans: ['Inter', 'system-ui', '-apple-system', 'sans-serif'],
        mono: ['Consolas', 'Cascadia Code', 'Fira Code', 'JetBrains Mono', 'monospace']
      }
    }
  },
  plugins: [
    require('daisyui')
  ],
  daisyui: {
    themes: [
      {
        // Dark theme – Aqua Gold matching the docs Aqua Gold theme.
        // Brand: #63dbe4 / #25ac8a / #dda122
        'sdvd-aqua': {
          'primary': '#63dbe4',
          'primary-content': '#0a1a1c',
          'secondary': '#25ac8a',
          'secondary-content': '#ffffff',
          'accent': '#dda122',
          'accent-content': '#1a1400',
          'neutral': '#1c2627',
          'neutral-content': '#c6e8e5',
          'base-100': '#222b30',
          'base-200': '#182024',
          'base-300': '#0d1315',
          'base-content': '#c6e0de',
          'info': '#63dbe4',
          'info-content': '#0a1a1c',
          'success': '#a6e3a1',
          'success-content': '#0a180a',
          'warning': '#dda122',
          'warning-content': '#1a1400',
          'error': '#f38ba8',
          'error-content': '#1a0a10',
          '--rounded-box': '0.75rem',
          '--rounded-btn': '0.5rem',
          '--rounded-badge': '1rem',
          '--tab-radius': '0.5rem',
        }
      },
      {
        'sdvd-light': {
          'primary': '#7c5cbf',
          'primary-content': '#ffffff',
          'secondary': '#5b21b6',
          'secondary-content': '#ffffff',
          'accent': '#34327a',
          'accent-content': '#ffffff',
          'neutral': '#f0eef6',
          'neutral-content': '#2d2b42',
          'base-100': '#ffffff',
          'base-200': '#f8f7fc',
          'base-300': '#f0eef6',
          'base-content': '#2d2b42',
          'info': '#3b82f6',
          'info-content': '#ffffff',
          'success': '#22c55e',
          'success-content': '#ffffff',
          'warning': '#eab308',
          'warning-content': '#1a1a2e',
          'error': '#ef4444',
          'error-content': '#ffffff',
          '--rounded-box': '0.75rem',
          '--rounded-btn': '0.5rem',
          '--rounded-badge': '1rem',
          '--tab-radius': '0.5rem',
        }
      }
    ],
    darkTheme: 'sdvd-aqua'
  }
}
