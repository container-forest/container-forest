/**
 * Ukrainian texts: the source of keys. Every other language must have exactly these keys (see en.ts), and
 * t() accepts only them, so a typo or a missing translation fails the typecheck.
 */
const uk = {
  app: {
    notFound: "Сторінку не знайдено.",
  },
  language: {
    switchTo: "Switch to English",
    other: "EN",
  },
  layout: {
    administrator: "(адміністратор)",
    signOut: "Вийти",
    nav: {
      discord: "Discord",
    },
  },
  home: {
    placeholder: "Панель у розробці: екрани з'являться тут.",
  },
  login: {
    title: "Вхід",
    description: "container-forest: керування контейнерами на ваших серверах.",
    userName: "Ім'я користувача",
    password: "Пароль",
    rememberMe: "Запам'ятати мене",
    submit: "Увійти",
    errors: {
      userNameRequired: "Введіть ім'я користувача.",
      passwordRequired: "Введіть пароль.",
      invalidCredentials: "Неправильне ім'я користувача або пароль.",
      failed: "Не вдалося ввійти. Перевірте з'єднання й спробуйте ще раз.",
    },
  },
  discord: {
    title: "Прив'язка Discord",
    description: "Після прив'язки бот container-forest у Discord діє від вашого імені й з вашими правами.",
    status: {
      title: "Стан",
      linked: "Прив'язано до {{name}}",
      linkedSince: "з {{date}}",
      notLinked: "Discord-акаунт не прив'язано.",
      loadFailed: "Не вдалося завантажити стан прив'язки.",
    },
    unlink: {
      button: "Відв'язати",
      confirm: "Бот перестане виконувати ваші команди. Відв'язати?",
      yes: "Так, відв'язати",
      cancel: "Скасувати",
      failed: "Не вдалося відв'язати акаунт.",
    },
    code: {
      title: "Прив'язати акаунт",
      titleRelink: "Прив'язати інший акаунт",
      instructions: "Отримайте одноразовий код і надішліть боту команду в Discord. Відповідь бота бачите лише ви.",
      get: "Отримати код",
      getNew: "Новий код",
      command: "Команда для Discord",
      copy: "Копіювати команду",
      copied: "Скопійовано",
      expiresIn: "Код дійсний ще {{time}}.",
      expired: "Термін дії коду минув. Отримайте новий.",
      waiting: "Чекаємо, поки бот прийме код…",
      failed: "Не вдалося отримати код.",
    },
  },
};

export type Translation = typeof uk;

export default uk;
