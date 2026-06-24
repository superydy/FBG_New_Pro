%% FBG 双光栅二维形状重构算法 
%

%
clear; close all; clc;

%% ==================== 1. 数据输入 ====================
% (数据来自您的脚本)
TOP_BEFORE = [1547.1671, 1535.7244, 1553.525, 1547.5988, 1535.6851, 1553.5817, 1547.6849, 1535.8338, 1553.6099, 1547.6581, 1535.8435, 1553.5883, 1547.6452, 1535.8076, 1553.5506, 1547.6702, 1535.7234, 1553.625, 1547.5904, 1535.7878, 1553.9968];
TOP_AFTER  = [1547.133, 1535.7339, 1553.5232, 1547.5937, 1535.6816, 1553.577, 1547.5138, 1535.5641, 1553.526, 1547.9527, 1536.4091, 1553.7781, 1547.5358, 1535.516, 1553.4155, 1547.6741, 1535.7221, 1553.623, 1547.5931, 1535.7898, 1553.9961];
BOT_BEFORE = [1554.0288, 1536.1572, 1548.0938, 1554.0418, 1536.1395, 1547.8826, 1553.6779, 1535.4674, 1546.9279, 1552.4111, 1534.0936, 1545.6601, 1551.0956, 1532.3437, 1547.5964, 1553.5768, 1535.8278, 1547.6485, 1553.5744, 1535.7708, 1547.6162];
BOT_AFTER  = [1554.0512, 1536.1501, 1548.0965, 1554.0483, 1536.1416, 1547.8841, 1553.795, 1535.7468, 1547.044, 1552.2354, 1533.4959, 1545.4158, 1551.1239, 1532.6375, 1547.7741, 1553.5749, 1535.8229, 1547.6493, 1553.5687, 1535.7736, 1547.6153];

%% ==================== 2. 参数配置 ====================
pe = 0.22;            % 光弹系数 
d_gratings = 0.003;   % 上下光栅之间的物理间距 (m)
spacing = 0.5;        % FBG测点间距 (m)
s_fine_step = 0.005;  % 重构曲线的插值步长 (m)
window_length = 7;    % (平滑窗口大小, 必须是奇数)

%% ==================== 3. 核心计算 ====================
fprintf('Running Coder-compatible EKF + Smoothdata (win=%d)...\n', window_length);
num_sensors = length(TOP_BEFORE);
sensor_positions = (0:num_sensors-1) * spacing;

% --- 应变计算 ---
eps_all = @(before, after) (after - before) ./ (before * (1 - pe));
strain_top = eps_all(TOP_BEFORE, TOP_AFTER);
strain_bottom = eps_all(BOT_BEFORE, BOT_AFTER);
axial_strain = 0.5 * (strain_top + strain_bottom); 
curvature = (strain_bottom - strain_top) / d_gratings;
curvature = curvature - mean(curvature); % (保留原始脚本的去均值)

s_fine = sensor_positions(1):s_fine_step:sensor_positions(end);

% --- (核心修改：使用 smoothdata 替换 csaps) ---
% 1. 先用 smoothdata 平滑原始的 21 个点
curvature_smooth_sgolay = smoothdata(curvature, 'sgolay', window_length);
% 2. 然后，在平滑后的点上进行插值
curvature_fine = interp1(sensor_positions, curvature_smooth_sgolay, s_fine, 'pchip');

% --- EKF 积分 ---
ds = s_fine_step;
num_points_fine = length(s_fine);
[x_coords, y_coords_m, beta_angle] = ...
    run_ekf(curvature_fine, ds, num_points_fine);

fprintf('  Max Deflection: %.4f cm\n', max(y_coords_m)*100);

%% ==================== 4. 结果可视化 ====================
figure('Position', [100, 100, 1200, 800]);

% 子图1: 原始应变数据
subplot(2,3,1);
plot(sensor_positions, strain_top*1e6, 'ro-', 'LineWidth', 1.5, 'MarkerSize', 6); hold on;
plot(sensor_positions, strain_bottom*1e6, 'bs-', 'LineWidth', 1.5, 'MarkerSize', 6);
plot(sensor_positions, axial_strain*1e6, 'k^-', 'LineWidth', 1.5, 'MarkerSize', 6);
xlabel('弧长位置 s (m)', 'FontSize', 11);
ylabel('应变 (με)', 'FontSize', 11);
title('应变分布', 'FontSize', 12, 'FontWeight', 'bold');
legend('上表面', '下表面', '轴向', 'Location', 'best');
grid on;

% 子图2: 曲率数据（原始 + 平滑）
subplot(2,3,2);
plot(sensor_positions, curvature, 'ko', 'MarkerSize', 8, 'LineWidth', 1.5); hold on;
plot(s_fine, curvature_fine, 'r--', 'LineWidth', 2); % (使用红色虚线)
xlabel('弧长位置 s (m)', 'FontSize', 11);
ylabel('曲率 κ (1/m)', 'FontSize', 11);
title('曲率分布 (Smoothdata 平滑)', 'FontSize', 12, 'FontWeight', 'bold');
legend('离散测点', sprintf('Smoothdata (win=%d)', window_length), 'Location', 'best');
grid on;

% 子图3: 角度分布
subplot(2,3,3);
plot(s_fine, rad2deg(beta_angle), 'r-', 'LineWidth', 2);
xlabel('弧长位置 s (m)', 'FontSize', 11);
ylabel('切线角 β (°)', 'FontSize', 11);
title('EKF 重构角度', 'FontSize', 12, 'FontWeight', 'bold');
grid on;

% 子图4: 2D 形状重构
subplot(2,3,[4,5,6]);
plot(x_coords, y_coords_m, 'r--', 'LineWidth', 2.5, 'DisplayName', '重构曲线'); hold on;
plot(x_coords(1), y_coords_m(1), 'go', 'MarkerSize', 12, 'LineWidth', 2, 'DisplayName', '起点');
plot(x_coords(end), y_coords_m(end), 'ro', 'MarkerSize', 12, 'LineWidth', 2, 'DisplayName', '终点');
% 添加 FBG 测点
y_at_sensors_m = interp1(s_fine, y_coords_m, sensor_positions, 'pchip');
scatter(x_coords(ismember(s_fine, sensor_positions)), y_at_sensors_m, ...
    60, 'k', 'filled', 's', 'DisplayName', 'FBG测点');

% 计算并标注最大位移
[max_disp_m, max_idx] = max(y_coords_m);
plot(x_coords(max_idx), max_disp_m, 'rp', 'MarkerSize', 12, 'MarkerFaceColor', 'r', ...
     'DisplayName', sprintf('最大挠度: %.2f cm', max_disp_m * 100));

xlabel('X 坐标 (m)', 'FontSize', 12);
ylabel('Y 坐标 (m)', 'FontSize', 12);
title('FBG 二维形状重构 (EKF + 边界修正)', 'FontSize', 14, 'FontWeight', 'bold');
legend('Location', 'best');
axis equal;
grid on;
sgtitle(sprintf('FBG 重构 - Coder 兼容版 (Smoothdata win=%d)', window_length), 'FontSize', 16, 'FontWeight', 'bold');

%% ==================== 5. 辅助函数 (EKF 积分器) ====================
% (我们将 EKF 循环封装到一个函数中，以便重用)
function [x_coords, y_coords_m, beta_angle] = run_ekf(curvature_fine, ds, num_points_fine)
    % 初始化结果数组
    y_coords_m = zeros(1, num_points_fine);
    beta_angle = zeros(1, num_points_fine);
    
    % 创建 EKF 对象
    ekf = extendedKalmanFilter(...
        @myStateFcn, ...
        @myMeasFcn);
    
    % 设置噪声矩阵
    ekf.MeasurementNoise = 0.1;
    ekf.ProcessNoise = diag([0.001, 0.01]);
    
    % 初始化状态和协方差
    ekf.State = [0; 0];
    ekf.StateCovariance = diag([1, 1]);
    
    % 设置雅可比函数
    ekf.StateTransitionJacobianFcn = @myStateJacobianFcn;
    ekf.MeasurementJacobianFcn = @myMeasJacobianFcn;
    
    % 存储初始状态
    y_coords_m(1) = ekf.State(1);
    beta_angle(1) = ekf.State(2);
    
    % EKF 主循环
    for k = 2:num_points_fine
        z_k = curvature_fine(k);
        kappa_k_minus_1 = curvature_fine(k-1);
        beta_k_minus_1 = ekf.State(2);
        
        predict(ekf, kappa_k_minus_1, ds);
        correct(ekf, z_k, beta_k_minus_1, ds);
        
        y_coords_m(k) = ekf.State(1);
        beta_angle(k) = ekf.State(2);
    end
    
    % 计算 x 坐标
    x_coords = cumtrapz(ds, cos(beta_angle));
    
    % 边界条件修正
    y_final_error = y_coords_m(end); 
    tilt_correction = linspace(0, y_final_error, num_points_fine);
    y_coords_m = y_coords_m - tilt_correction;
end

% --- EKF 辅助函数 ---
function x_k = myStateFcn(x_k_minus_1, kappa_k_minus_1, ds)
    y_k = x_k_minus_1(1) + sin(x_k_minus_1(2)) * ds;
    beta_k = x_k_minus_1(2) + kappa_k_minus_1 * ds;
    x_k = [y_k; beta_k];
end
function F = myStateJacobianFcn(x_k_minus_1, kappa_k_minus_1, ds)
    F = [1, cos(x_k_minus_1(2)) * ds;
         0, 1];
end
function z_k = myMeasFcn(x_k, beta_k_minus_1, ds)
    z_k = (x_k(2) - beta_k_minus_1) / ds;
end
function H = myMeasJacobianFcn(x_k, beta_k_minus_1, ds)
    H = [0, 1/ds];
end