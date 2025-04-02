create table users
(
    userId          INTEGER
        primary key autoincrement,
    login           TEXT,
    status          TEXT,
    phoneNumber     TEXT,
    passwordHash    TEXT,
    service         TEXT,
    portfolio       TEXT,
    userRespondedId integer,
    avatarPath      TEXT,
    isDeleted       BOOLEAN DEFAULT FALSE
);

create table orders
(
    orderId               INTEGER
        primary key autoincrement,
    userId                INTEGER
        references users,
    userRespondedId       INTEGER
        references users,
    description           TEXT,
    service               TEXT,
    cost                  REAL,
    comment               TEXT,
    dateRange             TEXT,
    title                 TEXT,
    photoPath             TEXT,
    file1Path             TEXT,
    file2Path             TEXT,
    file3Path             TEXT,
    status                TEXT,
    fileResultPath        TEXT,
    isDeleted             BOOLEAN DEFAULT FALSE
);

create table users_orders_actions
(
    orderId        INTEGER
        references orders,
    creatorOrderId INTEGER
        references users,
    fromUserId     INTEGER
        references users,
    toUserId       INTEGER
        references users,
    message        TEXT,
    actionName     TEXT,
    actionDate     DATETIME
);

