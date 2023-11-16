<?php
$_SERVER['REMOTE_ADDR'] = (!empty($_SERVER['HTTP_X_FORWARDED_FOR'])) ? $_SERVER['HTTP_X_FORWARDED_FOR'] :  $_SERVER['REMOTE_ADDR'];

include_once $_SERVER['DOCUMENT_ROOT'] . "/common/dbClass.info.php";

#[AllowDynamicProperties]
class DBCmp
{
    var $db_host = DB_DEV_HOST;
    var $db_info = array(
        "UID" => DB_DEV_ID,
        "PWD" => DB_DEV_PASS,
        "Database" => DB_DEV_DATABASE,
        "ConnectRetryCount" => 5,
        "LoginTimeout" => 10,
        "Encrypt" => TRUE,
        "TrustServerCertificate" => TRUE,
        "CharacterSet" => "UTF-8",
        'ReturnDatesAsStrings' => TRUE
    );


    var $db;
    var $conn;
    var $conn2;
    var $result;
    var $Msg;
    var $last_sql;
    var $dbTerms;

    var $is_dev_host = false;

    function __construct()
    {
        $this->is_dev_host = true;
        $this->conn = sqlsrv_connect($this->db_host, $this->db_info) or die("");
        $this->conn2 = sqlsrv_connect($this->db_host, $this->db_info) or die("");
    }

    public function __destruct()
    {
        sqlsrv_close($this->conn);
        sqlsrv_close($this->conn2);
    }

    function execute($sql, $type = null)
    {
        $sql2 = $sql;

        try {
            if (!empty($type)) {
                $this->result = sqlsrv_query($this->conn2, $sql2);
            } else {
                $this->result = sqlsrv_query($this->conn, $sql2);
            }
            if (!$this->result) {
                $errors = sqlsrv_errors();
                $error_log_arr = array();

                foreach ($errors as $error) {
                    $error_log_arr[] .= "SQLSTATE: " . $error['SQLSTATE'] . ", code: " . $error['code'] . ", message: " . $error['message'];
                }

                $list = array(
                    "error_log"      => implode(",", $error_log_arr),
                    "error_query"    => addslashes($sql2),
                    "error_location" => $_SERVER['PHP_SELF'],
                    "error_address"  => $_SERVER['REMOTE_ADDR'],
                    "log_date"       => "getdate()"
                );
                $this->store($list);
                $this->result = false;
            }
        } catch (Exception $e) {
            echo 'Message: ' . $e->getMessage();
        }
        return $this->result;
    }

    function multiExecute($sql)
    {
        $sql2 = $sql;

        try {
            $this->result = sqlsrv_query($this->conn, $sql2);
            if (!$this->result) {
                $errors = sqlsrv_errors();
                $list = array(
                    "error_log"      => "'" . str_replace("'", "''", $errors['SQLSTATE'] . $errors['code'] . $errors['message']) . "'",
                    "error_query"    => "'" . str_replace("'", "''", $sql2) . "'",
                    "error_location" => $_SERVER['PHP_SELF'],
                    "error_address"  => $_SERVER['REMOTE_ADDR'],
                    "log_date"       => "getdate()"
                );
                $this->store($list);
                $this->result = false;
            }
        } catch (Exception $e) {
            echo 'Message: ' . $e->getMessage();
        }
        return $this->result;
    }

    function sqlRow($sql, $type = null)
    {
        $rs = $this->execute($sql, $type);
        $data = sqlsrv_fetch_array($rs, SQLSRV_FETCH_ASSOC);
        return $data;
    }

    function sqlRowOne($sql, $type = null)
    {
        $rs = $this->execute($sql, $type);
        $data = sqlsrv_fetch_array($rs);

        return $data[0];
    }

    function sqlRowArr($sql, $type = null)
    {
        $rs = $this->execute($sql, $type);
        $return_data = array();
        if (!empty($rs) || $rs == true) {
            while ($data = sqlsrv_fetch_array($rs, SQLSRV_FETCH_ASSOC)) {
                $return_data[] = $data;
            }
        }

        return $return_data;
    }

    function sqlRowOneArr($sql, $type = null)
    {
        $rs = $this->execute($sql, $type);
        $return_data = array();
        while ($data = sqlsrv_fetch_array($rs, SQLSRV_FETCH_ASSOC)) {
            $return_data[] = $data[0];
        }
        return $return_data;
    }

    function getData($rs = "")
    {
        if ($rs) $result = $rs;
        else $result = $this->result;
        $data = (!empty($result)) ? sqlsrv_fetch_array($result) : null;
        return $data;
    }

    function getDataAssoc($rs = false)
    {
        if ($rs) $result = $rs;
        else $result = $this->result;

        $data = (!empty($result)) ? sqlsrv_fetch_array($result) : null;
        return $data;
    }

    //에러로그 저장 함수
    function store($list)
    {
        $key_arr = array();
        $val_arr = array();

        $sql = "INSERT INTO sql_log";
        foreach ($list as $key => $val) {
            $key_arr[] .= $key;
            if ($val === 'getdate()') {
                $val_arr[] .= "getdate()";
            } else {
                $val_arr[] .= "'" . str_replace("'", "''", $val) . "'";
            }
        }

        $key_str = implode(",", $key_arr);
        $val_str = implode(",", $val_arr);

        $sql = $sql . "(" . $key_str . ") VALUES (" . $val_str . ")";
        $result = $this->logExecute($sql);

        $this->last_sql = $sql;
        return $result;
    }

    function logExecute($sql)
    {
        $sql2 = $sql;
        $this->result = sqlsrv_query($this->conn, $sql2);

        return $this->result;
    }
}
