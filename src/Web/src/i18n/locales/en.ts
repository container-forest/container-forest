import type { Translation } from "./uk";

const en: Translation = {
  app: {
    notFound: "Page not found.",
  },
  language: {
    switchTo: "Перейти на українську",
    other: "УКР",
  },
  layout: {
    administrator: "(administrator)",
    signOut: "Sign out",
    nav: {
      discord: "Discord",
    },
  },
  home: {
    placeholder: "The panel is under construction: screens will appear here.",
  },
  login: {
    title: "Sign in",
    description: "container-forest: manage containers on your servers.",
    userName: "User name",
    password: "Password",
    rememberMe: "Remember me",
    submit: "Sign in",
    errors: {
      userNameRequired: "Enter your user name.",
      passwordRequired: "Enter your password.",
      invalidCredentials: "Incorrect user name or password.",
      failed: "Could not sign in. Check your connection and try again.",
    },
  },
  discord: {
    title: "Discord link",
    description: "Once linked, the container-forest bot in Discord acts for you, with your permissions.",
    status: {
      title: "Status",
      linked: "Linked to {{name}}",
      linkedSince: "since {{date}}",
      notLinked: "No Discord account is linked.",
      loadFailed: "Could not load the link status.",
    },
    unlink: {
      button: "Unlink",
      confirm: "The bot will stop running your commands. Unlink?",
      yes: "Yes, unlink",
      cancel: "Cancel",
      failed: "Could not unlink the account.",
    },
    code: {
      title: "Link an account",
      titleRelink: "Link another account",
      instructions: "Get a one-time code and send the bot this command in Discord. Only you see the bot's reply.",
      get: "Get a code",
      getNew: "New code",
      command: "Command for Discord",
      copy: "Copy the command",
      copied: "Copied",
      expiresIn: "The code is valid for {{time}} more.",
      expired: "The code has expired. Get a new one.",
      waiting: "Waiting for the bot to accept the code…",
      failed: "Could not get a code.",
    },
  },
};

export default en;
