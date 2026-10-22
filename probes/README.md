# Исследование ошибок OneScript

`probes` помогает искать, воспроизводить и фиксировать решения по нативным ошибкам OneScript. Результаты этого
исследования служат основанием для правил `native-errors`.

## С чего начать

Чтобы проверить сохраненные данные и увидеть следующий элемент для исследования, выполните:

```text
oscript .\probes\probes.os review next
```

Команда ничего не изменяет. Она также сообщит об осиротевших ручных решениях: записях, которым больше нет
соответствия в свежем отчете.

Для справки по всем командам используйте:

```text
oscript .\probes\probes.os --help
```

## Команды

| Задача | Команда | Меняет данные |
| --- | --- | --- |
| Проверить данные и показать следующие элементы обеих очередей | `review next` | Нет |
| Посмотреть сигнатуры исключений из исходников | `review source --limit 20` | Нет |
| Посмотреть внешние .NET-вызовы | `review external --limit 20` | Нет |
| Показать уже рассмотренные элементы очереди | `review source --include-reviewed` или `review external --include-reviewed` | Нет |
| Показать осиротевшие решения внешних границ | `review external --orphans` | Нет |
| Удалить осиротевшие решения `NotApplicable` | `review external --remove-not-applicable-orphans` | Да |
| Обновить статические отчеты после обновления исходников OneScript | `data refresh-source --engine-root C:\src\OneScript` | Да |
| Обновить отчеты выполнения после обновления движка или его тестов | `data refresh-runtime --engine-root C:\src\OneScript --oscript-path C:\bin\oscript.exe` | Да |
| Запустить тесты самих инструментов | `test` | Нет |

Полные примеры команд:

```text
oscript .\probes\probes.os review source --limit 20
oscript .\probes\probes.os review external --limit 20
oscript .\probes\probes.os test
```

Опции `--include-reviewed`, `--orphans` и `--remove-not-applicable-orphans` для
`review external`
взаимоисключающие.

## Как исследовать ошибку

1. Выполните `review next` или откройте нужную очередь командой `review source` либо
   `review external`.
2. Воспроизведите ошибку отдельным тестом или устойчивым сценарием.
3. Для правила нужны положительная и близкая отрицательная проверки. Статический внешний вызов сам по себе
   не доказывает ошибку выполнения.
4. Зафиксируйте результат вручную в соответствующем журнале в `data/`.
5. Добавьте правило и тест в основной проект, если сценарий стал кандидатом для классификации.

Разрешенные вызовы во внешней очереди сгруппированы по внешнему члену. Неразрешенные вызовы выводятся отдельно, так как
смещение IL и токен метаданных входят в их доказательство.

## Когда обновлять данные

Обычные команды просмотра не перегенерируют отчеты. Обновляйте данные только после обновления исходников,
движка или его тестов.

### Статические отчеты

```text
oscript .\probes\probes.os data refresh-source --engine-root C:\src\OneScript
```

Перед анализом команда соберет
`src/OneScript.Web.Server/OneScript.Web.Server.csproj` в конфигурации `Release`. Затем она обновит только:

- `data/source-error-report.json`;
- `data/external-call-boundaries.json`.

### Отчеты выполнения

```text
oscript .\probes\probes.os data refresh-runtime --engine-root C:\src\OneScript --oscript-path C:\bin\oscript.exe
```

Укажите путь к локальной копии исходников OneScript, содержащей `tests/testrunner.os`. Команда обновит только:

- `data/onescript-test-error-results.json`;
- `data/base-type-error-matrix-results.json`.

Каждое обновление сначала записывает результаты во временный каталог, проверяет контракт JSON и лишь затем публикует весь
набор отчетов. При ошибке уже замененные файлы восстанавливаются. Ручные журналы не перегенерируются.

После обновления сравните изменения JSON с предыдущей версией через Git. Новые записи `Unclassified` не считайте
автоматически пробелами классификации: сначала проверьте их вручную.

## Файлы данных

Все шесть JSON-файлов хранятся в репозитории.

| Файл | Кто изменяет | Назначение |
| --- | --- | --- |
| `data/source-error-report.json` | `refresh-source` | Места выброса исключений в исходниках OneScript |
| `data/source-error-review.json` | Разработчик | Решения по сигнатурам исходников |
| `data/external-call-boundaries.json` | `refresh-source` | Статический перечень внешних .NET-вызовов |
| `data/external-call-boundary-review.json` | Разработчик | Решения по внешним границам и доказательства выполнения |
| `data/onescript-test-error-results.json` | `refresh-runtime` | Результаты сценариев тестов движка |
| `data/base-type-error-matrix-results.json` | `refresh-runtime` | Матрица базовых типов для русской и английской локалей |

### Журнал внешних границ

В `external-call-boundary-review.json`:

- `Reviewed` содержит точечное решение для конкретного вызывающего члена;
- `Defaults` применяются только к разрешенным вызовам и ключуются внешней сборкой, типом и членом;
- точечное решение имеет приоритет над `Defaults`;
- ключ неразрешенной записи дополнительно включает `IlOffset` и `MetadataToken`;
- `Codes` и `Evidence` всегда массивы строк, `Note` всегда строка.

Команда `review external --remove-not-applicable-orphans` удаляет только осиротевшие записи со статусом
`NotApplicable`. Все остальные решения сохраняются.
