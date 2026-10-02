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
      containers: "Контейнери",
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
  containers: {
    title: "Контейнери",
    description: "Контейнери середовища, як їх зараз бачить агент. Список оновлюється кожні 15 секунд.",
    environment: "Середовище",
    offlineMark: " (офлайн)",
    noEnvironments: "Ви ще не бачите жодного середовища. Попросіть доступ в адміністратора.",
    agentOffline: "Агент середовища {{name}} офлайн: список контейнерів недоступний, дії вимкнено.",
    filters: {
      stack: "Стек",
      allStacks: "Усі стеки",
      noStack: "Поза стеками",
      state: "Стан",
      states: {
        all: "Усі",
        running: "Працюють",
        stopped: "Зупинені",
        other: "Інші",
      },
      search: "Пошук за назвою, образом або id",
      reset: "Скинути фільтри",
    },
    refresh: "Оновити",
    updatedAt: "оновлено о {{time}}",
    loading: "Завантаження контейнерів…",
    empty: "У середовищі немає контейнерів.",
    noMatches: "Жоден контейнер не відповідає фільтрам.",
    count: "Показано {{shown}} з {{total}}",
    columns: {
      name: "Назва",
      stack: "Стек / сервіс",
      state: "Стан",
      image: "Образ",
      created: "Створено",
      actions: "Дії",
    },
    states: {
      Unknown: "невідомий",
      Created: "створений",
      Running: "працює",
      Paused: "на паузі",
      Restarting: "перезапускається",
      Removing: "видаляється",
      Exited: "зупинений",
      Dead: "мертвий",
    },
    viewOnly: "лише перегляд",
    actions: {
      start: "Запустити",
      stop: "Зупинити",
      restart: "Перезапустити",
    },
    confirm: {
      stopTitle: "Зупинити {{name}}?",
      stopDescription: "Сервіс буде недоступний, доки контейнер не запустять знову.",
      restartTitle: "Перезапустити {{name}}?",
      restartDescription: "Сервіс буде недоступний, поки контейнер перезапускається.",
      timeout: "Час на коректну зупинку, с",
      timeoutHint: "Від 0 до {{max}}; 0 — зупинити одразу, без очікування.",
      timeoutInvalid: "Введіть ціле число від 0 до {{max}}.",
      cancel: "Скасувати",
    },
    done: {
      start: "Контейнер {{name}} запущено.",
      stop: "Контейнер {{name}} зупинено.",
      restart: "Контейнер {{name}} перезапущено.",
    },
    errors: {
      network: "Немає зв'язку з сервером. Перевірте з'єднання й спробуйте ще раз.",
      forbidden: "У вас немає прав на цю дію.",
      notFound: "Середовище або контейнер не знайдено: можливо, його вже видалили.",
      conflict: "Дія неможлива в поточному стані контейнера.",
      agentFailed: "Агент не зміг виконати дію.",
      agentOffline: "Агент середовища офлайн, дію не виконано.",
      agentTimeout: "Агент не відповів вчасно. Дія могла все ж виконатися: оновіть список, перш ніж повторювати.",
      unexpected: "Неочікувана помилка.",
      loadFailed: "Не вдалося завантажити контейнери.",
      loadEnvironmentsFailed: "Не вдалося завантажити середовища.",
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
