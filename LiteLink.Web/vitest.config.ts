import { defineConfig } from "vitest/config";

export default defineConfig({
  resolve: {
    alias: {
      // the browser lz4-wasm package has no Node-resolvable entry point;
      // the nodejs variant is API- and format-identical
      "lz4-wasm": "lz4-wasm-nodejs"
    }
  },
  test: {
    environment: "node"
  }
});
