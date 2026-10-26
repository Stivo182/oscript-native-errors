# native-errors

[![Release](https://img.shields.io/github/release/Stivo182/oscript-native-errors.svg)](https://github.com/Stivo182/oscript-native-errors/releases)
[![Тестирование](https://github.com/Stivo182/oscript-native-errors/actions/workflows/test.yml/badge.svg)](https://github.com/Stivo182/oscript-native-errors/actions/workflows/test.yml)
[![Used by](https://litrosbadges.ru/package/native-errors.svg)](https://litrosbadges.ru/package/native-errors)

Библиотека OneScript для нормализации исключений встроенных объектов. Она
содержит каталог нативных кодов и правил, а классификацию выполняет через
[`errors`](https://github.com/Stivo182/oscript-errors).

Поддерживаются, в частности, файловая система, процессы, потоки, JSON, XML,
текст, двоичные данные и HTTP, включая ошибки сети, прокси и TLS. Полный
перечень кодов, их назначение и область применения приведены в
[`КодахНативныхОшибок`](docs/КодыНативныхОшибок.md).

## Установка

```text
opm install native-errors
```

Требуется OneScript 2.0 или новее.

## Быстрый старт

```bsl
#Использовать errors
#Использовать native-errors

Классификатор = Новый КлассификаторОшибок;
Классификатор.ДобавитьПоставщикаПравил(Тип("ПравилаНативныхОшибок"));

Попытка
	Чтение = Новый ЧтениеJSON;
	Чтение.Прочитать();
Исключение
	Ошибка = Классификатор.Классифицировать(ИнформацияОбОшибке());
	Если Ошибка <> Неопределено Тогда
		Сообщить(ДиагностикаОшибок.КодОшибки(Ошибка)); // Будет выведено: `json.reader.not_open`
	КонецЕсли;
КонецПопытки;
```

Правила распознают известные сигнатуры нативных исключений OneScript. Ошибка
прикладного кода, внешней библиотеки или нативная ошибка с неподходящей
сигнатурой может остаться нераспознанной: тогда `КлассификаторОшибок` вернет
`Неопределено`. Наличие кода в каталоге не означает, что будет распознана любая
ошибка со сходным смыслом.

## Публичный API

- [`ПравилаНативныхОшибок`](docs/ПравилаНативныхОшибок.md)
- [`КодыНативныхОшибок`](docs/КодыНативныхОшибок.md)

## Разработка

Установить зависимости разработки:
```text
opm install -l --dev
```

Установить [OneUnit](https://github.com/sfaqer/OneUnit) и [BenchmarkOneScript](https://github.com/Stivo182/BenchmarkOneScript) при необходимости:
```text
opm install oneunit
opm install benchmark
```

Запустить тесты:
```text
oneunit execute
```

Запустить бенчмарки:
```text
benchos run .\benchmarks
```

Собрать пакет:
```text
opm build .
```
