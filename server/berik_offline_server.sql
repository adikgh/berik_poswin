-- Офлайн касса (Windows программасы) үшін серверге керек өзгерістер.
-- База: berik_003. Іске қоспас бұрын бэкап алыңыз. Бірнеше рет іске қосса да қауіпсіз (IF NOT EXISTS).

-- 1) Қай филиал офлайн точка екенін белгілеу (мысалы, Кафе основной)
ALTER TABLE `company_branch` ADD COLUMN IF NOT EXISTS `offline` TINYINT(1) NOT NULL DEFAULT 0;

-- 2) Байланған касса компьютерлері
CREATE TABLE IF NOT EXISTS `company_device` (
  `id` INT NOT NULL AUTO_INCREMENT,
  `company_id` INT NOT NULL,
  `branch_id` INT NOT NULL,
  `user_id` INT NULL,
  `name` VARCHAR(100) NULL,
  `token` CHAR(64) NOT NULL,
  `ver` VARCHAR(20) NULL,
  `ins_dt` DATETIME NULL,
  `last_dt` DATETIME NULL,
  `last_push_dt` DATETIME NULL,
  `last_ip` VARCHAR(45) NULL,
  `arh` TINYINT(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (`id`),
  UNIQUE KEY `token` (`token`),
  KEY `branch_id` (`branch_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 3) Офлайнда жасалған жазбаларды тану (қайта жүктелсе қосарланбайды)
ALTER TABLE `retail_orders`
  ADD COLUMN IF NOT EXISTS `offline_uuid` CHAR(36) NULL,
  ADD COLUMN IF NOT EXISTS `device_id` INT NULL,
  ADD UNIQUE INDEX IF NOT EXISTS `offline_uuid` (`offline_uuid`);

ALTER TABLE `retail_shifts`
  ADD COLUMN IF NOT EXISTS `offline_uuid` CHAR(36) NULL,
  ADD COLUMN IF NOT EXISTS `device_id` INT NULL,
  ADD UNIQUE INDEX IF NOT EXISTS `offline_uuid` (`offline_uuid`);

ALTER TABLE `retail_orders_log`
  ADD COLUMN IF NOT EXISTS `offline_uuid` CHAR(36) NULL,
  ADD UNIQUE INDEX IF NOT EXISTS `offline_uuid` (`offline_uuid`);
