module.exports = {
  preset: "jest-expo",
  watchman: false,
  testMatch: ["**/__tests__/**/*.test.ts?(x)"],
  moduleNameMapper: {
    "^@beauty/booking-core$": "<rootDir>/../packages/booking-core/src/index.ts",
    "^@/(.*)$": "<rootDir>/src/$1",
    // одна копія zod (з mobile/node_modules) і для core, який лежить поза projectRoot
    "^zod$": "<rootDir>/node_modules/zod",
  },
  // core - TypeScript-джерело поза rootDir: має транспілюватись
  transformIgnorePatterns: [
    "node_modules/(?!((jest-)?react-native|@react-native(-community)?|expo(nent)?|@expo(nent)?/.*|@expo-google-fonts/.*|react-navigation|@react-navigation/.*|@sentry/react-native|native-base|react-native-svg))",
  ],
};
