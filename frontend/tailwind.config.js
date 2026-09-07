/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        base: {
          950: "#05070d",
          900: "#0a0e1a",
          800: "#0f1424",
          700: "#161c30",
          600: "#212a45",
        },
        accent: {
          400: "#a78bfa",
          500: "#8b5cf6",
          600: "#7c3aed",
          700: "#6d28d9",
        },
        glow: {
          cyan: "#5eead4",
        },
      },
      fontFamily: {
        display: ["'Space Grotesk'", "system-ui", "sans-serif"],
        sans: ["'Inter'", "system-ui", "sans-serif"],
        mono: ["'IBM Plex Mono'", "monospace"],
      },
      boxShadow: {
        card: "0 1px 0 0 rgba(255,255,255,0.04) inset, 0 8px 24px -8px rgba(0,0,0,0.6)",
        glow: "0 0 40px -8px rgba(139, 92, 246, 0.45)",
      },
      backgroundImage: {
        "grid-fade":
          "linear-gradient(to bottom, rgba(139,92,246,0.06) 1px, transparent 1px), linear-gradient(to right, rgba(139,92,246,0.06) 1px, transparent 1px)",
      },
    },
  },
  plugins: [],
};
