import { defineConfig } from 'vite';
export default defineConfig({ build: { target: 'es2020', lib: { entry: 'src/main.ts', formats: ['es'], fileName: () => 'subtitles-tool.js' }, sourcemap: false } });
