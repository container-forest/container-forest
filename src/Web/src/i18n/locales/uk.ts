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
};

export type Translation = typeof uk;

export default uk;
