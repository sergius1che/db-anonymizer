import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  base: '/app/',
  build: { outDir: '../wwwroot/app', emptyOutDir: true },
  test: { environment: 'jsdom', setupFiles: './src/test-setup.js' },
});
