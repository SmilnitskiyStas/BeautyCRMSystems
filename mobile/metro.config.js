// Metro без root npm workspaces (mobile не входить у root `workspaces`; див. звіт/ADR-010 §5).
// Спільний пакет @beauty/booking-core читається як TypeScript-джерело з ../packages/booking-core:
//  - watchFolders: Metro стежить за пакетом поза projectRoot;
//  - extraNodeModules: аліас @beauty/booking-core -> джерело (без symlink у node_modules);
//  - disableHierarchicalLookup + nodeModulesPaths: усі залежності (react, zod, ...) беруться ЛИШЕ з mobile/node_modules,
//    щоб не підхопити другу копію react/zod з кореневого node_modules веб-фронтенду (дубль React = крах).
const { getDefaultConfig } = require("expo/metro-config");
const path = require("path");

const projectRoot = __dirname;
const corePath = path.resolve(projectRoot, "../packages/booking-core");

const config = getDefaultConfig(projectRoot);

config.watchFolders = [...(config.watchFolders ?? []), corePath];
config.resolver.nodeModulesPaths = [path.resolve(projectRoot, "node_modules")];
config.resolver.disableHierarchicalLookup = true;
config.resolver.extraNodeModules = {
  "@beauty/booking-core": path.resolve(corePath, "src"),
};

module.exports = config;
